"""ORM models of the salon_ml database (SPEC §8.2)."""

import uuid
from datetime import datetime
from decimal import Decimal
from typing import Any

from sqlalchemy import Boolean, DateTime, Float, ForeignKey, Index, Integer, Numeric, Text, desc
from sqlalchemy.dialects.postgresql import JSONB, UUID
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base

__all__ = [
    "Base",
    "ModelVersion",
    "Prediction",
    "PredictionRun",
    "RawAppointment",
    "RawCustomer",
    "SyncRun",
]


def _pk() -> Mapped[uuid.UUID]:
    return mapped_column(UUID(as_uuid=True), primary_key=True, default=uuid.uuid4)


class SyncRun(Base):
    __tablename__ = "sync_runs"

    id: Mapped[uuid.UUID] = _pk()
    started_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    status: Mapped[str] = mapped_column(Text)  # running | succeeded | failed
    customers_count: Mapped[int | None] = mapped_column(Integer)
    appointments_count: Mapped[int | None] = mapped_column(Integer)
    error: Mapped[str | None] = mapped_column(Text)


class RawCustomer(Base):
    __tablename__ = "raw_customers"

    customer_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))


class RawAppointment(Base):
    __tablename__ = "raw_appointments"

    appointment_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    customer_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True))
    employee_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True))
    service_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True))
    service_category: Mapped[str] = mapped_column(Text)
    starts_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    cancelled_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    status: Mapped[str] = mapped_column(Text)
    price: Mapped[Decimal] = mapped_column(Numeric(10, 2))


class ModelVersion(Base):
    __tablename__ = "model_versions"

    id: Mapped[uuid.UUID] = _pk()
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    algorithm: Mapped[str] = mapped_column(Text)
    params: Mapped[dict[str, Any]] = mapped_column(JSONB)
    metrics: Mapped[dict[str, Any]] = mapped_column(JSONB)
    baseline_metrics: Mapped[dict[str, Any]] = mapped_column(JSONB)
    candidates: Mapped[list[Any] | dict[str, Any]] = mapped_column(JSONB)
    feature_importances: Mapped[list[Any] | dict[str, Any]] = mapped_column(JSONB)
    n_train: Mapped[int] = mapped_column(Integer)
    n_valid: Mapped[int] = mapped_column(Integer)
    positive_rate: Mapped[float] = mapped_column(Float)
    artifact_path: Mapped[str] = mapped_column(Text)
    is_active: Mapped[bool] = mapped_column(Boolean, default=False)


class PredictionRun(Base):
    __tablename__ = "prediction_runs"

    id: Mapped[uuid.UUID] = _pk()
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    model_version_id: Mapped[uuid.UUID] = mapped_column(ForeignKey("model_versions.id"))
    reference_date: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    customers_scored: Mapped[int] = mapped_column(Integer)


class Prediction(Base):
    __tablename__ = "predictions"
    __table_args__ = (
        Index("ix_predictions_customer_id_created_at", "customer_id", desc("created_at")),
    )

    id: Mapped[uuid.UUID] = _pk()
    run_id: Mapped[uuid.UUID] = mapped_column(ForeignKey("prediction_runs.id"))
    customer_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True))
    churn_probability: Mapped[float] = mapped_column(Float)
    risk_level: Mapped[str] = mapped_column(Text)
    features: Mapped[dict[str, Any]] = mapped_column(JSONB)
    explanation: Mapped[str] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
