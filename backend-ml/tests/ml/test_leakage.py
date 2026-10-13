"""Mandatory leakage tests (SPEC §8.4). Do not skip or weaken."""

import numpy as np
import pandas as pd
import pytest

from app.ml.features import build_features
from tests.ml.synthetic import make_dataset

REF = pd.Timestamp("2026-10-12T12:00:00Z")
DAY = pd.Timedelta(days=1)


def _random_future_knowledge(appts: pd.DataFrame, cutoff, rng, n: int = 400) -> pd.DataFrame:
    """Appointments created after the cutoff: any start (also before it), any status."""
    ids = appts["customer_id"].unique()
    created = cutoff + rng.uniform(0.01, 200, n) * DAY
    starts = cutoff + rng.uniform(-300, 300, n) * DAY
    statuses = rng.choice(["Completed", "Cancelled", "NoShow", "Scheduled"], n)
    cancelled = [
        c + rng.uniform(0, 5) * DAY if s == "Cancelled" else pd.NaT
        for c, s in zip(created, statuses, strict=True)
    ]
    extra = pd.DataFrame(
        {
            "appointment_id": [f"x{i}" for i in range(n)],
            "customer_id": rng.choice(ids, n),
            "employee_id": "e0",
            "service_id": rng.choice(["s1", "s3", "s9"], n),
            "service_category": rng.choice(["Skin", "Nails", "Makeup"], n),
            "starts_at": starts,
            "created_at": created,
            "cancelled_at": cancelled,
            "status": statuses,
            "price": rng.uniform(10, 90, n),
        }
    )
    for col in ("starts_at", "created_at", "cancelled_at"):
        extra[col] = pd.to_datetime(extra[col], utc=True)
    return pd.concat([appts, extra], ignore_index=True)


@pytest.mark.parametrize("seed", [1, 2, 3])
@pytest.mark.parametrize("offset_days", [90, 210, 400])
def test_appointments_created_after_cutoff_do_not_change_features(seed, offset_days):
    customers, appts = make_dataset(120, REF, seed)
    cutoff = REF - offset_days * DAY
    baseline = build_features(appts, customers, cutoff)
    assert len(baseline) > 10  # the test must be meaningful

    rng = np.random.default_rng(seed + 100)
    polluted = _random_future_knowledge(appts, cutoff, rng)
    pd.testing.assert_frame_equal(build_features(polluted, customers, cutoff), baseline)


@pytest.mark.parametrize("seed", [1, 2])
def test_late_cancellations_are_invisible_before_they_happen(seed):
    customers, appts = make_dataset(120, REF, seed)
    cutoff = REF - 200 * DAY
    # Pretend that every cancellation known at the cutoff happened right after it, and that
    # the rows were Completed/NoShow originally: features must equal "never cancelled yet".
    late = appts.copy()
    mask = (late["status"] == "Cancelled") & (late["cancelled_at"] <= cutoff)
    late.loc[mask, "cancelled_at"] = cutoff + DAY
    as_if_scheduled = late.copy()
    as_if_scheduled.loc[mask, "status"] = "Scheduled"
    as_if_scheduled.loc[mask, "cancelled_at"] = pd.NaT
    assert mask.sum() > 0
    pd.testing.assert_frame_equal(
        build_features(late, customers, cutoff),
        build_features(as_if_scheduled, customers, cutoff),
    )
