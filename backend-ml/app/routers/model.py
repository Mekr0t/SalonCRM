import uuid
from datetime import datetime
from typing import Annotated, Any

from fastapi import APIRouter, Depends, HTTPException, Query
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.auth import CurrentUser, require_role
from app.db import get_db
from app.models import ModelVersion, PredictionRun, SyncRun
from app.schemas import CamelModel, CandidateOut, MetricsOut, candidates_out

router = APIRouter(prefix="/ml", tags=["model"])

admin_only = require_role("Admin")


class FeatureImportanceOut(CamelModel):
    feature: str
    importance: float


class ModelOut(CamelModel):
    id: uuid.UUID
    created_at: datetime
    algorithm: str
    params: dict[str, Any]
    metrics: MetricsOut
    baseline_metrics: MetricsOut
    candidates: list[CandidateOut]
    feature_importances: list[FeatureImportanceOut]
    n_train: int
    n_valid: int
    positive_rate: float


class SyncRunOut(CamelModel):
    id: uuid.UUID
    started_at: datetime
    finished_at: datetime | None
    status: str
    customers_count: int | None
    appointments_count: int | None
    error: str | None


class ModelVersionSummary(CamelModel):
    id: uuid.UUID
    created_at: datetime
    algorithm: str
    roc_auc: float | None
    baseline_roc_auc: float | None
    is_active: bool


class PredictionRunOut(CamelModel):
    id: uuid.UUID
    created_at: datetime
    model_version_id: uuid.UUID
    reference_date: datetime
    customers_scored: int


class RunsOut(CamelModel):
    sync_runs: list[SyncRunOut]
    model_versions: list[ModelVersionSummary]
    prediction_runs: list[PredictionRunOut]


def _importance_pairs(stored: dict | list) -> list[tuple[str, float]]:
    if isinstance(stored, dict):
        pairs = list(stored.items())
    else:
        pairs = [(i["feature"], i["importance"]) for i in stored]
    return sorted(pairs, key=lambda p: -p[1])


@router.get(
    "/model",
    response_model=ModelOut,
    response_model_by_alias=True,
    summary="Active model: metrics, baseline, candidates, feature importances",
    responses={404: {"description": "No trained model yet"}},
)
def active_model(_: CurrentUser = Depends(admin_only), db: Session = Depends(get_db)) -> ModelOut:
    m = db.scalars(
        select(ModelVersion)
        .where(ModelVersion.is_active)
        .order_by(ModelVersion.created_at.desc())
        .limit(1)
    ).first()
    if m is None:
        raise HTTPException(status_code=404, detail="Zatiaľ neexistuje natrénovaný model.")
    return ModelOut(
        id=m.id,
        created_at=m.created_at,
        algorithm=m.algorithm,
        params=m.params,
        metrics=MetricsOut(**m.metrics),
        baseline_metrics=MetricsOut(**m.baseline_metrics),
        candidates=candidates_out(m.candidates),
        feature_importances=[
            FeatureImportanceOut(feature=f, importance=v)
            for f, v in _importance_pairs(m.feature_importances)
        ],
        n_train=m.n_train,
        n_valid=m.n_valid,
        positive_rate=m.positive_rate,
    )


@router.get(
    "/runs",
    response_model=RunsOut,
    response_model_by_alias=True,
    summary="History of sync, training and prediction runs (newest first)",
)
def runs(
    limit: Annotated[int, Query(ge=1, le=100)] = 20,
    _: CurrentUser = Depends(admin_only),
    db: Session = Depends(get_db),
) -> RunsOut:
    syncs = db.scalars(select(SyncRun).order_by(SyncRun.started_at.desc()).limit(limit)).all()
    models = db.scalars(
        select(ModelVersion).order_by(ModelVersion.created_at.desc()).limit(limit)
    ).all()
    pruns = db.scalars(
        select(PredictionRun).order_by(PredictionRun.created_at.desc()).limit(limit)
    ).all()
    return RunsOut(
        sync_runs=[SyncRunOut.model_validate(s, from_attributes=True) for s in syncs],
        model_versions=[
            ModelVersionSummary(
                id=m.id,
                created_at=m.created_at,
                algorithm=m.algorithm,
                roc_auc=m.metrics.get("roc_auc"),
                baseline_roc_auc=m.baseline_metrics.get("roc_auc"),
                is_active=m.is_active,
            )
            for m in models
        ],
        prediction_runs=[PredictionRunOut.model_validate(r, from_attributes=True) for r in pruns],
    )
