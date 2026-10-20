import uuid
from datetime import UTC, datetime

from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext
from sqlalchemy import select, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from app.db import Base
from app.models import ModelVersion, Prediction, PredictionRun


def test_models_match_migrated_schema(db_engine: Engine) -> None:
    with db_engine.connect() as conn:
        diff = compare_metadata(MigrationContext.configure(conn), Base.metadata)
    assert diff == []


def test_predictions_index_is_descending(db_engine: Engine) -> None:
    with db_engine.connect() as conn:
        definitions = (
            conn.execute(text("SELECT indexdef FROM pg_indexes WHERE tablename = 'predictions'"))
            .scalars()
            .all()
        )
    assert any("(customer_id, created_at DESC)" in d for d in definitions)


def test_prediction_roundtrip_with_jsonb(db_session: Session) -> None:
    now = datetime.now(UTC)
    model = ModelVersion(
        created_at=now,
        algorithm="logreg",
        params={"C": 1.0},
        metrics={"auc": 0.8},
        baseline_metrics={"auc": 0.5},
        candidates=[{"name": "logreg"}],
        feature_importances={"recency_days": 0.4},
        n_train=100,
        n_valid=20,
        positive_rate=0.3,
        artifact_path="models/x.joblib",
        is_active=True,
    )
    db_session.add(model)
    db_session.flush()
    run = PredictionRun(
        created_at=now, model_version_id=model.id, reference_date=now, customers_scored=1
    )
    db_session.add(run)
    db_session.flush()
    customer_id = uuid.uuid4()
    db_session.add(
        Prediction(
            run_id=run.id,
            customer_id=customer_id,
            churn_probability=0.71,
            risk_level="High",
            features={"visits": 3, "tags": ["a"]},
            explanation="Long gap since last visit.",
            created_at=now,
        )
    )
    db_session.commit()

    row = db_session.execute(
        select(Prediction).where(Prediction.customer_id == customer_id)
    ).scalar_one()
    assert row.features == {"visits": 3, "tags": ["a"]}
    assert row.created_at == now
    assert row.churn_probability == 0.71
