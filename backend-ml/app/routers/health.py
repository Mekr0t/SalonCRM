from typing import Literal

from fastapi import APIRouter
from sqlalchemy import exists, select, text

from app.db import SessionLocal
from app.models import ModelVersion
from app.schemas import CamelModel

router = APIRouter(prefix="/ml", tags=["health"])


class HealthResponse(CamelModel):
    status: Literal["ok", "degraded"]
    database: Literal["ok", "unavailable"]
    active_model: bool


def _probe() -> tuple[bool, bool]:
    """Return (database reachable, active model exists); never raises."""
    try:
        with SessionLocal() as session:
            session.execute(text("SELECT 1"))
            active = bool(session.scalar(select(exists().where(ModelVersion.is_active))))
        return True, active
    except Exception:  # noqa: BLE001 - health must never raise
        return False, False


@router.get("/health", response_model=HealthResponse, response_model_by_alias=True)
def health() -> HealthResponse:
    database_ok, active = _probe()
    if database_ok:
        return HealthResponse(status="ok", database="ok", active_model=active)
    return HealthResponse(status="degraded", database="unavailable", active_model=False)
