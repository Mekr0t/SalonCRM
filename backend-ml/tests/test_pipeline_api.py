"""Pipeline and ML endpoints against real PostgreSQL; main is simulated via httpx.MockTransport."""

import uuid
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import httpx
import pandas as pd
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from app.clients.main_api import MainApiClient, get_main_api
from app.config import get_settings
from app.ml.train import TrainingResult
from app.ml.train import train as real_train
from app.models import (
    ModelVersion,
    Prediction,
    PredictionRun,
    RawAppointment,
    RawCustomer,
    SyncRun,
)
from app.pipeline import PIPELINE_LOCK_KEY, utc_now
from tests.ml.synthetic import make_dataset

REF = pd.Timestamp("2026-10-12T12:00:00Z")
NAMESPACE = uuid.UUID("12345678-1234-5678-1234-567812345678")
ADMIN = {"active": True, "userId": str(uuid.uuid4()), "role": "Admin", "fullName": "Eva Admin"}
USER = {"active": True, "userId": str(uuid.uuid4()), "role": "User", "fullName": "Uma User"}

ADMIN_ENDPOINTS: list[tuple[str, str]] = [
    ("POST", "/ml/pipeline/run"),
    ("GET", "/ml/predictions"),
    ("GET", "/ml/model"),
    ("GET", "/ml/runs"),
    ("GET", "/ml/stats/risk-distribution"),
]
USER_ENDPOINTS: list[tuple[str, str]] = [
    ("GET", f"/ml/predictions/{uuid.uuid4()}"),
    ("POST", "/ml/predictions/lookup"),
    ("GET", f"/ml/predictions/{uuid.uuid4()}/history"),
]


# Training on an identical dataset is deterministic (tests/ml/test_train.py), so the pipeline
# tests reuse one TrainingResult per (dataset, kwargs) instead of refitting the candidates.
_TRAIN_CACHE: dict[tuple[Any, ...], TrainingResult] = {}


def cached_train(dataset: pd.DataFrame, **kwargs: Any) -> TrainingResult:
    key = (
        int(pd.util.hash_pandas_object(dataset, index=True).sum()),
        dataset.shape,
        tuple(sorted(kwargs.items())),
    )
    if key not in _TRAIN_CACHE:
        _TRAIN_CACHE[key] = real_train(dataset, **kwargs)
    return _TRAIN_CACHE[key]


@pytest.fixture(autouse=True)
def memoized_training(request: pytest.FixtureRequest, monkeypatch: pytest.MonkeyPatch) -> None:
    if "real_training" not in request.fixturenames:
        monkeypatch.setattr("app.pipeline.train", cached_train)


@pytest.fixture
def real_training(monkeypatch: pytest.MonkeyPatch) -> None:
    """Uncached training path, for the end-to-end pipeline test."""
    monkeypatch.setattr("app.pipeline.train", real_train)


def _uid(name: str) -> str:
    return str(uuid.uuid5(NAMESPACE, name))


def _iso(value: Any) -> str | None:
    return None if pd.isna(value) else value.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def export_payload(customers: pd.DataFrame, appts: pd.DataFrame) -> dict[str, Any]:
    """Synthetic frames as a SPEC section 7 export (camelCase, ISO UTC, uuid strings)."""
    return {
        "customers": [
            {"customerId": _uid(r.customer_id), "createdAt": _iso(r.created_at)}
            for r in customers.itertuples()
        ],
        "appointments": [
            {
                "appointmentId": _uid(r.appointment_id),
                "customerId": _uid(r.customer_id),
                "employeeId": _uid(r.employee_id),
                "serviceId": _uid(r.service_id),
                "serviceCategory": r.service_category,
                "startsAt": _iso(r.starts_at),
                "createdAt": _iso(r.created_at),
                "cancelledAt": _iso(r.cancelled_at),
                "status": r.status,
                "price": float(r.price),
            }
            for r in appts.itertuples()
        ],
    }


class FakeMain:
    """Programmable main backend: introspection by cookie value, export payload or failure."""

    def __init__(self) -> None:
        self.export: dict[str, Any] | Exception | None = None

    def __call__(self, request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/introspect"):
            token = request.content.decode()
            if "admin-token" in token:
                return httpx.Response(200, json=ADMIN)
            if "user-token" in token:
                return httpx.Response(200, json=USER)
            return httpx.Response(200, json={"active": False})
        if isinstance(self.export, Exception):
            raise self.export
        return httpx.Response(200, json=self.export)


@pytest.fixture(scope="module")
def synthetic_export() -> dict[str, Any]:
    return export_payload(*make_dataset(200, REF, seed=42))


@pytest.fixture
def main(synthetic_export: dict[str, Any]) -> FakeMain:
    fake = FakeMain()
    fake.export = synthetic_export
    return fake


@pytest.fixture
def api(
    client: TestClient, main: FakeMain, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> Iterator[TestClient]:
    from app.main import app

    monkeypatch.setattr(get_settings(), "model_dir", str(tmp_path / "models"))
    main_client = MainApiClient("http://main", "k", transport=httpx.MockTransport(main))
    app.dependency_overrides[get_main_api] = lambda: main_client
    app.dependency_overrides[utc_now] = lambda: REF.to_pydatetime()
    yield client


def as_admin(api: TestClient) -> TestClient:
    api.cookies.clear()
    api.cookies.set("salon_sid", "admin-token")
    return api


def as_user(api: TestClient) -> TestClient:
    api.cookies.clear()
    api.cookies.set("salon_sid", "user-token")
    return api


@pytest.fixture
def first_run(api: TestClient) -> dict[str, Any]:
    response = as_admin(api).post("/ml/pipeline/run")
    assert response.status_code == 200, response.text
    return response.json()


def count(session: Session, model: type) -> int:
    return session.scalar(select(func.count()).select_from(model)) or 0


# --- pipeline --------------------------------------------------------------------------------


def test_pipeline_run_populates_database(
    api: TestClient,
    real_training: None,
    first_run: dict[str, Any],
    db_session: Session,
    synthetic_export: dict[str, Any],
    tmp_path: Path,
) -> None:
    body = first_run
    print(
        f"\nwinner={body['algorithm']} auc={body['metrics']['rocAuc']:.3f} "
        f"baseline_auc={body['baselineMetrics']['rocAuc']:.3f} beats={body['beatsBaseline']}"
    )
    assert body["customersCount"] == len(synthetic_export["customers"])
    assert body["appointmentsCount"] == len(synthetic_export["appointments"])
    assert body["metrics"]["rocAuc"] is not None
    assert {"confusionMatrix", "positiveRate", "precision", "recall", "f1", "n"} <= body[
        "metrics"
    ].keys()
    assert body["baselineMetrics"]["rocAuc"] is not None
    assert isinstance(body["beatsBaseline"], bool)
    assert body["beatsBaseline"] == (body["metrics"]["rocAuc"] > body["baselineMetrics"]["rocAuc"])
    assert len(body["candidates"]) == 3
    assert {c["algorithm"] for c in body["candidates"]} >= {body["algorithm"]}
    assert body["referenceDate"].startswith("2026-10-12T12:00:00")
    assert isinstance(body["warnings"], list)
    assert body["durationSeconds"] >= 0

    sync = db_session.scalars(select(SyncRun)).one()
    assert sync.status == "succeeded"
    assert sync.finished_at is not None and sync.error is None
    assert str(sync.id) == body["syncRunId"]
    assert count(db_session, RawCustomer) == body["customersCount"]
    assert count(db_session, RawAppointment) == body["appointmentsCount"]
    assert sync.customers_count == body["customersCount"]

    model = db_session.scalars(select(ModelVersion)).one()
    assert model.is_active and str(model.id) == body["modelVersionId"]
    assert Path(model.artifact_path).is_file()
    assert Path(model.artifact_path).parent == tmp_path / "models"
    assert model.metrics["roc_auc"] == pytest.approx(body["metrics"]["rocAuc"])
    assert "roc_auc" in model.baseline_metrics  # DB keeps the snake_case form

    run = db_session.scalars(select(PredictionRun)).one()
    assert run.customers_scored == body["customersScored"] > 0
    assert run.model_version_id == model.id
    predictions = db_session.scalars(select(Prediction)).all()
    assert len(predictions) == body["customersScored"]
    assert {p.risk_level for p in predictions} <= {"low", "medium", "high"}
    assert all(p.explanation and 0 <= p.churn_probability <= 1 for p in predictions)
    assert all("recency_ratio" in p.features for p in predictions)


def test_second_run_replaces_raw_and_accumulates_history(
    api: TestClient, first_run: dict[str, Any], db_session: Session
) -> None:
    second = as_admin(api).post("/ml/pipeline/run")
    assert second.status_code == 200
    body = second.json()
    assert count(db_session, RawCustomer) == body["customersCount"]
    assert count(db_session, RawAppointment) == body["appointmentsCount"]
    assert count(db_session, SyncRun) == 2
    models = db_session.scalars(select(ModelVersion).order_by(ModelVersion.created_at)).all()
    assert [m.is_active for m in models] == [False, True]
    assert count(db_session, PredictionRun) == 2
    assert count(db_session, Prediction) == first_run["customersScored"] + body["customersScored"]


def test_concurrent_run_is_409(api: TestClient, clean_db: Engine) -> None:
    with clean_db.connect() as other:
        other.execute(text("SELECT pg_advisory_lock(:k)"), {"k": PIPELINE_LOCK_KEY})
        try:
            response = as_admin(api).post("/ml/pipeline/run")
        finally:
            other.execute(text("SELECT pg_advisory_unlock(:k)"), {"k": PIPELINE_LOCK_KEY})
    assert response.status_code == 409
    assert response.json()["detail"] == "Pipeline už beží."
    assert response.headers["content-type"].startswith("application/problem+json")
    # The lock is free again afterwards.
    assert as_admin(api).post("/ml/pipeline/run").status_code == 200


def test_main_unavailable_is_503_and_sync_failed(
    api: TestClient, main: FakeMain, db_session: Session
) -> None:
    main.export = httpx.ConnectError("down")
    response = as_admin(api).post("/ml/pipeline/run")
    assert response.status_code == 503
    assert response.headers["content-type"].startswith("application/problem+json")
    sync = db_session.scalars(select(SyncRun)).one()
    assert sync.status == "failed" and sync.error and sync.finished_at is not None
    assert count(db_session, ModelVersion) == 0


@pytest.mark.parametrize("payload", [{"customers": "x", "appointments": []}, {"customers": []}])
def test_malformed_export_is_502(
    api: TestClient, main: FakeMain, db_session: Session, payload: dict[str, Any]
) -> None:
    main.export = payload
    response = as_admin(api).post("/ml/pipeline/run")
    assert response.status_code == 502
    assert response.json()["title"] == "Bad Gateway"
    assert db_session.scalars(select(SyncRun)).one().status == "failed"
    assert count(db_session, RawCustomer) == 0


def test_invalid_uuid_in_export_is_502(api: TestClient, main: FakeMain) -> None:
    main.export = {
        "customers": [{"customerId": "nope", "createdAt": "2026-01-01T00:00:00Z"}],
        "appointments": [],
    }
    assert as_admin(api).post("/ml/pipeline/run").status_code == 502


def test_too_little_data_is_422_and_keeps_sync(
    api: TestClient, main: FakeMain, db_session: Session
) -> None:
    main.export = export_payload(*make_dataset(3, REF, seed=1))
    response = as_admin(api).post("/ml/pipeline/run")
    assert response.status_code == 422
    assert response.headers["content-type"].startswith("application/problem+json")
    assert "dát" in response.json()["detail"]
    assert db_session.scalars(select(SyncRun)).one().status == "succeeded"
    assert count(db_session, RawCustomer) == 3
    assert count(db_session, ModelVersion) == 0
    assert count(db_session, Prediction) == 0


def test_empty_export_is_422(api: TestClient, main: FakeMain) -> None:
    main.export = {"customers": [], "appointments": []}
    assert as_admin(api).post("/ml/pipeline/run").status_code == 422


def test_unwritable_model_dir_is_500_and_previous_model_stays_active(
    api: TestClient,
    first_run: dict[str, Any],
    db_session: Session,
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    blocker = tmp_path / "file"
    blocker.write_text("a regular file, not a directory")
    monkeypatch.setattr(get_settings(), "model_dir", str(blocker / "models"))
    response = as_admin(api).post("/ml/pipeline/run")
    assert response.status_code == 500
    assert response.headers["content-type"].startswith("application/problem+json")
    body = response.json()
    assert body["status"] == 500
    assert body["detail"] == "Model sa nepodarilo uložiť (MODEL_DIR nie je zapisovateľný)."

    assert count(db_session, ModelVersion) == 1
    model = db_session.scalars(select(ModelVersion)).one()
    assert model.is_active and str(model.id) == first_run["modelVersionId"]
    assert count(db_session, PredictionRun) == 1
    assert count(db_session, Prediction) == first_run["customersScored"]


def test_unexpected_training_error_is_500_not_422(
    api: TestClient, db_session: Session, monkeypatch: pytest.MonkeyPatch
) -> None:
    from app.main import app

    def broken_train(dataset: pd.DataFrame, **kwargs: Any) -> TrainingResult:
        raise ValueError("boom")

    monkeypatch.setattr("app.pipeline.train", broken_train)
    client = TestClient(app, raise_server_exceptions=False)
    response = as_admin(client).post("/ml/pipeline/run")
    assert response.status_code == 500
    assert response.headers["content-type"].startswith("application/problem+json")
    assert response.json()["detail"] == "Unexpected server error."
    assert count(db_session, ModelVersion) == 0
    assert count(db_session, Prediction) == 0


# --- endpoints ---------------------------------------------------------------------------------


def test_health_active_model_flag(api: TestClient) -> None:
    assert api.get("/ml/health").json()["activeModel"] is False
    as_admin(api).post("/ml/pipeline/run")
    body = api.get("/ml/health").json()
    assert body["activeModel"] is True and body["status"] == "ok"


def test_model_404_before_run_and_200_after(api: TestClient) -> None:
    response = as_admin(api).get("/ml/model")
    assert response.status_code == 404
    assert response.headers["content-type"].startswith("application/problem+json")
    assert "model" in response.json()["detail"]


def test_model_after_run(api: TestClient, first_run: dict[str, Any]) -> None:
    body = as_admin(api).get("/ml/model").json()
    assert body["id"] == first_run["modelVersionId"]
    assert body["algorithm"] == first_run["algorithm"]
    assert body["metrics"]["rocAuc"] == pytest.approx(first_run["metrics"]["rocAuc"])
    assert len(body["candidates"]) == 3
    importances = [i["importance"] for i in body["featureImportances"]]
    assert importances == sorted(importances, reverse=True) and len(importances) > 10
    assert {"feature", "importance"} == body["featureImportances"][0].keys()
    assert body["nTrain"] > 0 and body["nValid"] > 0 and 0 < body["positiveRate"] < 1


def test_list_predictions_order_filter_pagination(
    api: TestClient, first_run: dict[str, Any]
) -> None:
    admin = as_admin(api)
    page = admin.get("/ml/predictions", params={"limit": 500}).json()
    assert page["total"] == len(page["items"]) == first_run["customersScored"]
    probs = [i["churnProbability"] for i in page["items"]]
    assert probs == sorted(probs, reverse=True)
    assert {"customerId", "churnProbability", "riskLevel", "explanation", "createdAt"} <= page[
        "items"
    ][0].keys()
    assert page["items"][0]["predictionRunId"] == first_run["predictionRunId"]

    ids = [i["customerId"] for i in page["items"]]
    small = admin.get("/ml/predictions", params={"limit": 5, "offset": 3}).json()
    assert small["total"] == page["total"]
    assert [i["customerId"] for i in small["items"]] == ids[3:8]

    for level in ("low", "medium", "high"):
        filtered = admin.get("/ml/predictions", params={"risk": level, "limit": 500}).json()
        assert filtered["total"] == len(filtered["items"])
        assert {i["riskLevel"] for i in filtered["items"]} <= {level}
    assert admin.get("/ml/predictions", params={"risk": "extreme"}).status_code == 422
    assert admin.get("/ml/predictions", params={"limit": 0}).status_code == 422
    assert admin.get("/ml/predictions", params={"limit": 501}).status_code == 422
    assert admin.get("/ml/predictions", params={"offset": -1}).status_code == 422


def test_list_returns_only_latest_prediction_per_customer(
    api: TestClient, first_run: dict[str, Any]
) -> None:
    second = as_admin(api).post("/ml/pipeline/run").json()
    page = api.get("/ml/predictions", params={"limit": 500}).json()
    assert page["total"] == second["customersScored"]
    assert {i["predictionRunId"] for i in page["items"]} == {second["predictionRunId"]}


def test_detail_with_features_and_unknown_customer(
    api: TestClient, first_run: dict[str, Any]
) -> None:
    top = as_user(api).get("/ml/predictions", params={"limit": 1})
    assert top.status_code == 403  # list is admin only
    top = as_admin(api).get("/ml/predictions", params={"limit": 1}).json()["items"][0]
    detail = as_user(api).get(f"/ml/predictions/{top['customerId']}").json()
    assert detail["churnProbability"] == top["churnProbability"]
    assert detail["explanation"]
    assert detail["features"]["visit_count"] >= 2
    assert "recency_ratio" in detail["features"]

    unknown = as_user(api).get(f"/ml/predictions/{uuid.uuid4()}")
    assert unknown.status_code == 200
    body = unknown.json()
    assert body["riskLevel"] == "insufficient_data"
    assert body["churnProbability"] is None and body["explanation"] is None
    assert body["features"] is None and body["createdAt"] is None
    assert as_user(api).get("/ml/predictions/not-a-uuid").status_code == 422


def test_lookup_order_dedupe_unknown_and_limit(api: TestClient, first_run: dict[str, Any]) -> None:
    items = as_admin(api).get("/ml/predictions", params={"limit": 3}).json()["items"]
    unknown = str(uuid.uuid4())
    requested = [items[2]["customerId"], unknown, items[0]["customerId"], items[2]["customerId"]]
    response = as_user(api).post("/ml/predictions/lookup", json={"customerIds": requested})
    assert response.status_code == 200
    result = response.json()["items"]
    assert [r["customerId"] for r in result] == [requested[0], unknown, requested[2]]
    assert result[0]["churnProbability"] == items[2]["churnProbability"]
    assert result[1]["riskLevel"] == "insufficient_data" and result[1]["churnProbability"] is None
    assert result[2]["riskLevel"] == items[0]["riskLevel"]

    assert as_user(api).post("/ml/predictions/lookup", json={"customerIds": []}).json() == {
        "items": []
    }
    too_many = [str(uuid.uuid4()) for _ in range(501)]
    over = as_user(api).post("/ml/predictions/lookup", json={"customerIds": too_many})
    assert over.status_code == 422
    assert over.headers["content-type"].startswith("application/problem+json")
    ok = [str(uuid.uuid4()) for _ in range(500)]
    assert as_user(api).post("/ml/predictions/lookup", json={"customerIds": ok}).status_code == 200


def test_lookup_uses_single_query(
    api: TestClient, first_run: dict[str, Any], clean_db: Engine
) -> None:
    from sqlalchemy import event

    statements: list[str] = []

    def record(conn, cursor, statement, *args):  # noqa: ANN001, ARG001
        statements.append(statement)

    event.listen(clean_db, "before_cursor_execute", record)
    try:
        ids = [str(uuid.uuid4()) for _ in range(50)]
        as_user(api).post("/ml/predictions/lookup", json={"customerIds": ids})
    finally:
        event.remove(clean_db, "before_cursor_execute", record)
    assert sum("predictions" in s for s in statements) == 1


def test_history_ascending_after_two_runs(api: TestClient, first_run: dict[str, Any]) -> None:
    second = as_admin(api).post("/ml/pipeline/run").json()
    customer = as_admin(api).get("/ml/predictions", params={"limit": 1}).json()["items"][0]
    body = as_user(api).get(f"/ml/predictions/{customer['customerId']}/history").json()
    assert body["customerId"] == customer["customerId"]
    assert [i["predictionRunId"] for i in body["items"]] == [
        first_run["predictionRunId"],
        second["predictionRunId"],
    ]
    stamps = [i["createdAt"] for i in body["items"]]
    assert stamps == sorted(stamps) and stamps[0] != stamps[1]
    assert {"createdAt", "churnProbability", "riskLevel", "predictionRunId"} == body["items"][
        0
    ].keys()

    empty = as_user(api).get(f"/ml/predictions/{uuid.uuid4()}/history").json()
    assert empty["items"] == []


def test_runs_listing(api: TestClient, first_run: dict[str, Any]) -> None:
    second = as_admin(api).post("/ml/pipeline/run").json()
    body = api.get("/ml/runs").json()
    assert [s["id"] for s in body["syncRuns"]] == [second["syncRunId"], first_run["syncRunId"]]
    assert body["syncRuns"][0]["status"] == "succeeded"
    models = body["modelVersions"]
    assert [m["id"] for m in models] == [second["modelVersionId"], first_run["modelVersionId"]]
    assert [m["isActive"] for m in models] == [True, False]
    assert {"algorithm", "rocAuc", "baselineRocAuc", "createdAt"} <= models[0].keys()
    runs = body["predictionRuns"]
    assert [r["id"] for r in runs] == [second["predictionRunId"], first_run["predictionRunId"]]
    assert runs[0]["modelVersionId"] == second["modelVersionId"]
    assert runs[0]["customersScored"] == second["customersScored"]
    assert len(api.get("/ml/runs", params={"limit": 1}).json()["syncRuns"]) == 1
    assert api.get("/ml/runs", params={"limit": 101}).status_code == 422


def test_risk_distribution(api: TestClient, first_run: dict[str, Any]) -> None:
    body = as_admin(api).get("/ml/stats/risk-distribution").json()
    assert body["total"] == first_run["customersCount"]
    assert body["low"] + body["medium"] + body["high"] == first_run["customersScored"]
    assert body["insufficientData"] == first_run["customersCount"] - first_run["customersScored"]
    assert body["low"] + body["medium"] + body["high"] + body["insufficientData"] == body["total"]
    assert body["lastPredictionAt"] is not None
    page = api.get("/ml/predictions", params={"risk": "high", "limit": 500}).json()
    assert page["total"] == body["high"]


def test_risk_distribution_without_predictions(api: TestClient) -> None:
    body = as_admin(api).get("/ml/stats/risk-distribution").json()
    assert body == {
        "low": 0,
        "medium": 0,
        "high": 0,
        "insufficientData": 0,
        "total": 0,
        "lastPredictionAt": None,
    }


# --- auth ----------------------------------------------------------------------------------------


def _call(api: TestClient, method: str, path: str) -> httpx.Response:
    kwargs: dict[str, Any] = {"json": {"customerIds": []}} if path.endswith("lookup") else {}
    return api.request(method, path, **kwargs)


@pytest.mark.parametrize(("method", "path"), [*ADMIN_ENDPOINTS, *USER_ENDPOINTS])
def test_endpoints_require_a_session(api: TestClient, method: str, path: str) -> None:
    api.cookies.clear()
    response = _call(api, method, path)
    assert response.status_code == 401
    assert response.headers["content-type"].startswith("application/problem+json")
    api.cookies.set("salon_sid", "unknown-token")
    assert _call(api, method, path).status_code == 401


@pytest.mark.parametrize(("method", "path"), ADMIN_ENDPOINTS)
def test_admin_endpoints_forbid_user(api: TestClient, method: str, path: str) -> None:
    response = _call(as_user(api), method, path)
    assert response.status_code == 403
    assert response.headers["content-type"].startswith("application/problem+json")


@pytest.mark.parametrize(("method", "path"), USER_ENDPOINTS)
def test_user_endpoints_allow_user(api: TestClient, method: str, path: str) -> None:
    assert _call(as_user(api), method, path).status_code == 200


def test_openapi_documents_endpoints(api: TestClient) -> None:
    assert api.get("/docs").status_code == 200
    paths = api.get("/openapi.json").json()["paths"]
    expected = {
        "/ml/health",
        "/ml/pipeline/run",
        "/ml/predictions",
        "/ml/predictions/{customer_id}",
        "/ml/predictions/lookup",
        "/ml/predictions/{customer_id}/history",
        "/ml/model",
        "/ml/runs",
        "/ml/stats/risk-distribution",
    }
    assert expected <= paths.keys()
