import uuid
from datetime import datetime
from typing import Annotated, Any, Literal

from fastapi import APIRouter, Depends, Query
from pydantic import Field
from sqlalchemy import Select, func, select
from sqlalchemy.dialects.postgresql import distinct_on
from sqlalchemy.orm import Session, aliased

from app.auth import CurrentUser, require_role
from app.db import get_db
from app.models import Prediction
from app.schemas import CamelModel

router = APIRouter(prefix="/ml", tags=["predictions"])

admin_only = require_role("Admin")
any_user = require_role("User", "Admin")

INSUFFICIENT = "insufficient_data"


class PredictionItem(CamelModel):
    customer_id: uuid.UUID
    churn_probability: float | None
    risk_level: str
    explanation: str | None
    created_at: datetime | None
    prediction_run_id: uuid.UUID | None


class PredictionDetail(PredictionItem):
    # Keys are the SPEC 8.4 feature names (data identifiers, stay snake_case).
    features: dict[str, float] | None


class PredictionPage(CamelModel):
    items: list[PredictionItem]
    total: int


class LookupRequest(CamelModel):
    customer_ids: list[uuid.UUID] = Field(max_length=500)


class LookupResponse(CamelModel):
    items: list[PredictionItem]


class HistoryPoint(CamelModel):
    created_at: datetime
    churn_probability: float
    risk_level: str
    prediction_run_id: uuid.UUID


class HistoryResponse(CamelModel):
    customer_id: uuid.UUID
    items: list[HistoryPoint]


def latest_predictions(customer_ids: list[uuid.UUID] | None = None) -> Any:
    """Aliased entity over the newest prediction of each customer (DISTINCT ON subquery)."""
    inner: Select = select(Prediction).ext(distinct_on(Prediction.customer_id))
    if customer_ids is not None:
        inner = inner.where(Prediction.customer_id.in_(customer_ids))
    inner = inner.order_by(Prediction.customer_id, Prediction.created_at.desc(), Prediction.id)
    return aliased(Prediction, inner.subquery())


def _item(p: Any) -> PredictionItem:
    return PredictionItem(
        customer_id=p.customer_id,
        churn_probability=p.churn_probability,
        risk_level=p.risk_level,
        explanation=p.explanation,
        created_at=p.created_at,
        prediction_run_id=p.run_id,
    )


def _insufficient(customer_id: uuid.UUID) -> PredictionItem:
    return PredictionItem(
        customer_id=customer_id,
        churn_probability=None,
        risk_level=INSUFFICIENT,
        explanation=None,
        created_at=None,
        prediction_run_id=None,
    )


@router.get(
    "/predictions",
    response_model=PredictionPage,
    response_model_by_alias=True,
    summary="Latest prediction per customer, highest risk first",
)
def list_predictions(
    risk: Literal["low", "medium", "high"] | None = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 50,
    offset: Annotated[int, Query(ge=0)] = 0,
    _: CurrentUser = Depends(admin_only),
    db: Session = Depends(get_db),
) -> PredictionPage:
    latest = latest_predictions()
    filters = [latest.risk_level == risk] if risk else []
    total = db.scalar(select(func.count()).select_from(latest).where(*filters)) or 0
    rows = db.scalars(
        select(latest)
        .where(*filters)
        .order_by(latest.churn_probability.desc(), latest.customer_id)
        .limit(limit)
        .offset(offset)
    ).all()
    return PredictionPage(items=[_item(p) for p in rows], total=total)


@router.get(
    "/predictions/{customer_id}",
    response_model=PredictionDetail,
    response_model_by_alias=True,
    summary="Latest prediction of one customer with features and explanation",
)
def get_prediction(
    customer_id: uuid.UUID,
    _: CurrentUser = Depends(any_user),
    db: Session = Depends(get_db),
) -> PredictionDetail:
    p = db.scalars(
        select(Prediction)
        .where(Prediction.customer_id == customer_id)
        .order_by(Prediction.created_at.desc(), Prediction.id)
        .limit(1)
    ).first()
    if p is None:
        return PredictionDetail(**_insufficient(customer_id).model_dump(), features=None)
    return PredictionDetail(**_item(p).model_dump(), features=p.features)


@router.post(
    "/predictions/lookup",
    response_model=LookupResponse,
    response_model_by_alias=True,
    summary="Latest predictions for up to 500 customers (calendar badges)",
)
def lookup_predictions(
    body: LookupRequest,
    _: CurrentUser = Depends(any_user),
    db: Session = Depends(get_db),
) -> LookupResponse:
    ids = list(dict.fromkeys(body.customer_ids))
    latest = latest_predictions(ids)
    found = {p.customer_id: p for p in db.scalars(select(latest)).all()}
    return LookupResponse(items=[_item(found[i]) if i in found else _insufficient(i) for i in ids])


@router.get(
    "/predictions/{customer_id}/history",
    response_model=HistoryResponse,
    response_model_by_alias=True,
    summary="Churn probability over time (ascending)",
)
def prediction_history(
    customer_id: uuid.UUID,
    _: CurrentUser = Depends(any_user),
    db: Session = Depends(get_db),
) -> HistoryResponse:
    rows = db.scalars(
        select(Prediction)
        .where(Prediction.customer_id == customer_id)
        .order_by(Prediction.created_at, Prediction.id)
    ).all()
    return HistoryResponse(
        customer_id=customer_id,
        items=[
            HistoryPoint(
                created_at=p.created_at,
                churn_probability=p.churn_probability,
                risk_level=p.risk_level,
                prediction_run_id=p.run_id,
            )
            for p in rows
        ],
    )
