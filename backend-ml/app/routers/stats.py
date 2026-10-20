from datetime import datetime

from fastapi import APIRouter, Depends
from sqlalchemy import exists, func, select
from sqlalchemy.orm import Session

from app.auth import CurrentUser, require_role
from app.db import get_db
from app.models import Prediction, RawCustomer
from app.routers.predictions import latest_predictions
from app.schemas import CamelModel

router = APIRouter(prefix="/ml", tags=["stats"])

admin_only = require_role("Admin")


class RiskDistribution(CamelModel):
    low: int
    medium: int
    high: int
    insufficient_data: int
    total: int
    last_prediction_at: datetime | None


@router.get(
    "/stats/risk-distribution",
    response_model=RiskDistribution,
    response_model_by_alias=True,
    summary="Customer counts by risk level (latest prediction per customer)",
)
def risk_distribution(
    _: CurrentUser = Depends(admin_only), db: Session = Depends(get_db)
) -> RiskDistribution:
    latest = latest_predictions()
    rows = db.execute(select(latest.risk_level, func.count()).group_by(latest.risk_level)).all()
    counts = {level: n for level, n in rows}
    insufficient = (
        db.scalar(
            select(func.count())
            .select_from(RawCustomer)
            .where(~exists().where(Prediction.customer_id == RawCustomer.customer_id))
        )
        or 0
    )
    last = db.scalar(select(func.max(Prediction.created_at)))
    low, medium, high = (counts.get(k, 0) for k in ("low", "medium", "high"))
    return RiskDistribution(
        low=low,
        medium=medium,
        high=high,
        insufficient_data=insufficient,
        total=low + medium + high + insufficient,
        last_prediction_at=last,
    )
