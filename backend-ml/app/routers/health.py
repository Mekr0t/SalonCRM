from typing import Literal

from fastapi import APIRouter
from sqlalchemy import text

from app.db import engine
from app.schemas import CamelModel

router = APIRouter(prefix="/ml", tags=["health"])


class HealthResponse(CamelModel):
    status: Literal["ok", "degraded"]
    database: Literal["ok", "unavailable"]


def _database_ok() -> bool:
    try:
        with engine.connect() as conn:
            conn.execute(text("SELECT 1"))
        return True
    except Exception:  # noqa: BLE001 - health must never raise
        return False


@router.get("/health", response_model=HealthResponse, response_model_by_alias=True)
def health() -> HealthResponse:
    if _database_ok():
        return HealthResponse(status="ok", database="ok")
    return HealthResponse(status="degraded", database="unavailable")
