import os
from collections.abc import Iterator
from pathlib import Path

import pytest
from alembic.config import Config
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session
from testcontainers.community.postgres import PostgresContainer

from alembic import command

ALEMBIC_INI = Path(__file__).resolve().parent.parent / "alembic.ini"


@pytest.fixture(scope="session")
def db_engine() -> Iterator[Engine]:
    """Real PostgreSQL (Testcontainers) with the schema created by `alembic upgrade head`."""
    with PostgresContainer("postgres:17", dbname="salon_ml", driver="psycopg") as postgres:
        url = postgres.get_connection_url()
        # alembic/env.py reads the URL from the cached settings.
        os.environ["DATABASE_URL"] = url
        from app import db
        from app.config import get_settings

        get_settings.cache_clear()
        command.upgrade(Config(str(ALEMBIC_INI)), "head")

        engine = create_engine(url, pool_pre_ping=True)
        # app.db builds its engine at import time; point the app at the test database.
        db.engine.dispose()
        db.engine = engine
        db.SessionLocal.configure(bind=engine)
        yield engine
        engine.dispose()


@pytest.fixture
def clean_db(db_engine: Engine) -> Engine:
    """Empties every table before the test runs."""
    from app.db import Base

    tables = ", ".join(f'"{t.name}"' for t in Base.metadata.sorted_tables)
    with db_engine.begin() as conn:
        conn.execute(text(f"TRUNCATE {tables} RESTART IDENTITY CASCADE"))
    return db_engine


@pytest.fixture
def db_session(clean_db: Engine) -> Iterator[Session]:
    with Session(clean_db, expire_on_commit=False) as session:
        yield session


@pytest.fixture
def client(clean_db: Engine) -> Iterator[TestClient]:
    from app.auth import clear_auth_cache
    from app.main import app

    clear_auth_cache()
    with TestClient(app) as test_client:
        yield test_client
    app.dependency_overrides.clear()
    clear_auth_cache()
