# ORM models live here; tables are added in later phases (see docs/SPEC.md §8).
# Alembic env.py imports this module so that Base.metadata is complete for autogenerate.
from app.db import Base

__all__ = ["Base"]
