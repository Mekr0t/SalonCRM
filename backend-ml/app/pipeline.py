"""Pipeline orchestration: sync -> train -> predict (SPEC §8.3)."""

import logging
import time
import uuid
from collections.abc import Iterator
from contextlib import contextmanager, suppress
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import pandas as pd
from fastapi import HTTPException
from sqlalchemy import delete, insert, text, update

from app import db
from app.clients.main_api import MainApiClient, MainApiUnavailable
from app.config import Settings
from app.ml.explain import explain
from app.ml.features import build_features
from app.ml.frame import from_export
from app.ml.labels import build_dataset
from app.ml.predict import predict_proba, risk_level
from app.ml.train import InsufficientDataError, TrainingResult, save_model, train
from app.models import (
    ModelVersion,
    Prediction,
    PredictionRun,
    RawAppointment,
    RawCustomer,
    SyncRun,
)
from app.schemas import CamelModel, CandidateOut, MetricsOut, candidates_out

logger = logging.getLogger(__name__)

# Arbitrary constant key of the session-level advisory lock guarding the pipeline.
PIPELINE_LOCK_KEY = 5_100_001
MAX_ERROR_LENGTH = 500


class PipelineResult(CamelModel):
    sync_run_id: uuid.UUID
    model_version_id: uuid.UUID
    prediction_run_id: uuid.UUID
    reference_date: datetime
    customers_count: int
    appointments_count: int
    customers_scored: int
    algorithm: str
    metrics: MetricsOut
    baseline_metrics: MetricsOut
    candidates: list[CandidateOut]
    beats_baseline: bool
    warnings: list[str]
    duration_seconds: float


def utc_now() -> datetime:
    """Clock of the pipeline (FastAPI dependency; tests override it)."""
    return datetime.now(UTC)


def _wall_clock() -> datetime:
    """Real time for row timestamps (ordering of history), independent of the reference date."""
    return datetime.now(UTC)


@contextmanager
def pipeline_lock() -> Iterator[None]:
    """Cross-process exclusion via a PostgreSQL advisory lock; 409 if it is already held."""
    conn = db.engine.connect().execution_options(isolation_level="AUTOCOMMIT")
    try:
        acquired = conn.execute(
            text("SELECT pg_try_advisory_lock(:key)"), {"key": PIPELINE_LOCK_KEY}
        ).scalar()
        if not acquired:
            raise HTTPException(status_code=409, detail="Pipeline už beží.")
        try:
            yield
        finally:
            try:
                conn.execute(text("SELECT pg_advisory_unlock(:key)"), {"key": PIPELINE_LOCK_KEY})
            except Exception:  # noqa: BLE001 - never return a connection that may hold the lock
                conn.invalidate()
    finally:
        conn.close()


def _short(message: str) -> str:
    return message[:MAX_ERROR_LENGTH]


def _py_time(value: Any) -> datetime | None:
    return None if pd.isna(value) else pd.Timestamp(value).to_pydatetime()


def _customer_rows(customers: pd.DataFrame) -> list[dict[str, Any]]:
    if customers["customer_id"].duplicated().any():
        raise ValueError("Export 'customers': duplicate customerId")
    return [
        {"customer_id": uuid.UUID(str(r.customer_id)), "created_at": _py_time(r.created_at)}
        for r in customers.itertuples()
    ]


def _appointment_rows(appts: pd.DataFrame) -> list[dict[str, Any]]:
    if appts["appointment_id"].duplicated().any():
        raise ValueError("Export 'appointments': duplicate appointmentId")
    return [
        {
            "appointment_id": uuid.UUID(str(r.appointment_id)),
            "customer_id": uuid.UUID(str(r.customer_id)),
            "employee_id": uuid.UUID(str(r.employee_id)),
            "service_id": uuid.UUID(str(r.service_id)),
            "service_category": str(r.service_category),
            "starts_at": _py_time(r.starts_at),
            "created_at": _py_time(r.created_at),
            "cancelled_at": _py_time(r.cancelled_at),
            "status": str(r.status),
            "price": float(r.price),
        }
        for r in appts.itertuples()
    ]


def _finish_sync(sync_id: uuid.UUID, **values: Any) -> None:
    with db.SessionLocal() as session:
        session.execute(
            update(SyncRun).where(SyncRun.id == sync_id).values(finished_at=_wall_clock(), **values)
        )
        session.commit()


def sync_from_main(main_api: MainApiClient) -> tuple[uuid.UUID, pd.DataFrame, pd.DataFrame]:
    """Download the export and replace raw_* in one transaction; returns (sync id, frames)."""
    sync_id = uuid.uuid4()
    with db.SessionLocal() as session:
        session.add(SyncRun(id=sync_id, started_at=_wall_clock(), status="running"))
        session.commit()
    try:
        payload = main_api.fetch_export()
        try:
            customers, appts = from_export(payload)
            customer_rows = _customer_rows(customers)
            appointment_rows = _appointment_rows(appts)
        except (ValueError, TypeError, KeyError, AttributeError) as exc:
            raise HTTPException(
                status_code=502,
                detail=_short(f"Export z hlavnej služby má neplatný formát: {exc}"),
            ) from exc
        with db.SessionLocal() as session, session.begin():
            session.execute(delete(RawAppointment))
            session.execute(delete(RawCustomer))
            # Core bulk insert: the ORM path takes seconds for a few thousand rows.
            if customer_rows:
                session.execute(insert(RawCustomer.__table__), customer_rows)
            if appointment_rows:
                session.execute(insert(RawAppointment.__table__), appointment_rows)
            session.execute(
                update(SyncRun)
                .where(SyncRun.id == sync_id)
                .values(
                    status="succeeded",
                    finished_at=_wall_clock(),
                    customers_count=len(customer_rows),
                    appointments_count=len(appointment_rows),
                    error=None,
                )
            )
    except MainApiUnavailable as exc:
        _finish_sync(sync_id, status="failed", error=_short(str(exc)))
        raise HTTPException(
            status_code=503, detail="Hlavná služba nie je dostupná, synchronizácia zlyhala."
        ) from exc
    except HTTPException as exc:
        _finish_sync(sync_id, status="failed", error=_short(str(exc.detail)))
        raise
    except Exception as exc:
        logger.exception("Sync failed")
        _finish_sync(sync_id, status="failed", error=_short(f"{type(exc).__name__} during sync"))
        raise
    return sync_id, customers, appts


def train_model(
    customers: pd.DataFrame, appts: pd.DataFrame, reference: pd.Timestamp, settings: Settings
) -> TrainingResult:
    """Build the snapshot dataset and train; 422 when the history is insufficient."""
    try:
        dataset = build_dataset(
            appts,
            customers,
            reference,
            settings.horizon_days,
            settings.snapshot_count,
            settings.snapshot_step_days,
        )
        if dataset.empty:
            raise InsufficientDataError("empty dataset")
        return train(
            dataset,
            gap=settings.validation_gap_snapshots,
            horizon_days=settings.horizon_days,
            random_state=settings.random_state,
        )
    except InsufficientDataError as exc:
        logger.warning("Training skipped: %s", exc)
        raise HTTPException(
            status_code=422,
            detail=(
                "Nedostatok dát na natrénovanie modelu: je potrebná dostatočne dlhá história "
                "zákazníkov s aspoň 2 dokončenými návštevami, v ktorej sa vyskytujú "
                "aj zákazníci, ktorí sa vrátili, aj tí, ktorí neprišli."
            ),
        ) from exc


def store_model_and_predictions(
    result: TrainingResult,
    customers: pd.DataFrame,
    appts: pd.DataFrame,
    reference: pd.Timestamp,
    settings: Settings,
) -> tuple[uuid.UUID, uuid.UUID, int]:
    """Save the artifact, activate the new model and write one prediction run atomically."""
    model_id, run_id = uuid.uuid4(), uuid.uuid4()
    model_dir = Path(settings.model_dir)
    artifact = model_dir / f"{model_id}.joblib"
    try:
        model_dir.mkdir(parents=True, exist_ok=True)
        save_model(result.model, artifact, result.algorithm)
    except OSError as exc:
        logger.exception("Cannot save model artifact to %s", artifact)
        with suppress(OSError):
            artifact.unlink(missing_ok=True)
        raise HTTPException(
            status_code=500,
            detail="Model sa nepodarilo uložiť (MODEL_DIR nie je zapisovateľný).",
        ) from exc
    try:
        features = build_features(appts, customers, reference)
        proba = predict_proba(result.model, features)
        created = _wall_clock()
        rows = []
        for customer_id, p in proba.items():
            row = {k: float(v) for k, v in features.loc[customer_id].items()}
            rows.append(
                {
                    "id": uuid.uuid4(),
                    "run_id": run_id,
                    "customer_id": uuid.UUID(str(customer_id)),
                    "churn_probability": float(p),
                    "risk_level": risk_level(
                        float(p), settings.risk_medium_threshold, settings.risk_high_threshold
                    ),
                    "features": row,
                    "explanation": explain(row),
                    "created_at": created,
                }
            )
        with db.SessionLocal() as session, session.begin():
            session.execute(update(ModelVersion).values(is_active=False))
            session.add(
                ModelVersion(
                    id=model_id,
                    created_at=created,
                    algorithm=result.algorithm,
                    params=result.params,
                    metrics=result.metrics,
                    baseline_metrics=result.baseline_metrics,
                    candidates=result.candidates,
                    feature_importances=result.feature_importances,
                    n_train=result.n_train,
                    n_valid=result.n_valid,
                    positive_rate=result.positive_rate,
                    artifact_path=str(artifact),
                    is_active=True,
                )
            )
            session.flush()
            session.add(
                PredictionRun(
                    id=run_id,
                    created_at=created,
                    model_version_id=model_id,
                    reference_date=reference.to_pydatetime(),
                    customers_scored=len(rows),
                )
            )
            session.flush()
            if rows:
                session.execute(insert(Prediction), rows)
    except Exception:
        artifact.unlink(missing_ok=True)
        raise
    return model_id, run_id, len(rows)


def run_pipeline(main_api: MainApiClient, settings: Settings, now: datetime) -> PipelineResult:
    started = time.perf_counter()
    reference = pd.Timestamp(now).tz_convert("UTC")
    with pipeline_lock():
        sync_id, customers, appts = sync_from_main(main_api)
        result = train_model(customers, appts, reference, settings)
        model_id, run_id, scored = store_model_and_predictions(
            result, customers, appts, reference, settings
        )
    auc, base_auc = result.metrics["roc_auc"], result.baseline_metrics["roc_auc"]
    return PipelineResult(
        sync_run_id=sync_id,
        model_version_id=model_id,
        prediction_run_id=run_id,
        reference_date=reference.to_pydatetime(),
        customers_count=len(customers),
        appointments_count=len(appts),
        customers_scored=scored,
        algorithm=result.algorithm,
        metrics=MetricsOut(**result.metrics),
        baseline_metrics=MetricsOut(**result.baseline_metrics),
        candidates=candidates_out(result.candidates),
        beats_baseline=auc is not None and base_auc is not None and auc > base_auc,
        warnings=result.warnings,
        duration_seconds=round(time.perf_counter() - started, 3),
    )
