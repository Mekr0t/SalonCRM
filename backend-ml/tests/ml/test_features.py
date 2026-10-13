import math

import pandas as pd
import pytest

from app.ml.features import CATEGORY_COLUMNS, FEATURE_COLUMNS, build_features, known_at

CUTOFF = pd.Timestamp("2026-01-01T00:00:00Z")


def ts(value: str) -> pd.Timestamp:
    return pd.Timestamp(value, tz="UTC")


def appt(aid, cust, start, *, status="Completed", created=None, cancelled=None,
         service="n1", category="Nails", price=30.0):  # fmt: skip
    start = ts(start)
    return {
        "appointment_id": aid,
        "customer_id": cust,
        "employee_id": "e1",
        "service_id": service,
        "service_category": category,
        "starts_at": start,
        "created_at": ts(created) if created else start - pd.Timedelta(days=7),
        "cancelled_at": ts(cancelled) if cancelled else pd.NaT,
        "status": status,
        "price": price,
    }


def frame(rows):
    df = pd.DataFrame(rows)
    for col in ("starts_at", "created_at", "cancelled_at"):
        df[col] = pd.to_datetime(df[col], utc=True)
    return df


CUSTOMERS = pd.DataFrame(
    {
        "customer_id": ["A", "B", "C", "D", "E"],
        "created_at": [ts("2025-09-01")] * 5,
    }
)
APPOINTMENTS = frame(
    [
        # A: gaps 20, 41, 21 days
        appt("a1", "A", "2025-10-01T10:00:00Z", price=30),
        appt("a2", "A", "2025-10-21T10:00:00Z", price=40),
        appt("a3", "A", "2025-12-01T10:00:00Z", service="sk1", category="Skin", price=50),
        appt("a4", "A", "2025-12-22T10:00:00Z", service="n2", price=30),
        appt("a5", "A", "2025-11-10T10:00:00Z", status="Cancelled", cancelled="2025-11-05"),
        appt("a6", "A", "2025-11-20T10:00:00Z", status="NoShow"),
        # cancelled only after the cutoff -> counts only in the cancel_rate denominator
        appt("a7", "A", "2025-12-10T10:00:00Z", status="Cancelled", cancelled="2026-01-05"),
        # future booking, created before the cutoff, not cancelled
        appt("a8", "A", "2026-01-10T10:00:00Z", status="Scheduled", created="2025-12-20"),
        # B: two visits on the same day; both future bookings must NOT count
        appt("b1", "B", "2025-12-01T09:00:00Z", service="w1", category="Waxing", price=20),
        appt("b2", "B", "2025-12-01T15:00:00Z", service="w1", category="Waxing", price=30),
        appt("b3", "B", "2026-01-20T10:00:00Z", status="Scheduled", created="2026-01-02"),
        appt(
            "b4",
            "B",
            "2026-01-15T10:00:00Z",
            status="Cancelled",
            created="2025-12-10",
            cancelled="2025-12-15",
        ),  # fmt: skip
        # C: a single visit -> excluded
        appt("c1", "C", "2025-12-01T10:00:00Z"),
        # D: old customer, cancelled-after-cutoff future booking still counts
        appt("d1", "D", "2025-06-01T10:00:00Z", service="m1", category="Massage", price=55),
        appt("d2", "D", "2025-07-01T10:00:00Z", service="m1", category="Massage", price=55),
        appt(
            "d3",
            "D",
            "2026-02-01T10:00:00Z",
            status="Cancelled",
            created="2025-12-20",
            cancelled="2026-01-03",
        ),  # fmt: skip
        # E: category tie (Skin 1 x, Nails 1 x) -> alphabetically first = Nails
        appt("e1", "E", "2025-12-01T10:00:00Z", service="sk1", category="Skin"),
        appt("e2", "E", "2025-12-11T10:00:00Z", service="n1", category="Nails"),
    ]
)


@pytest.fixture(scope="module")
def features():
    return build_features(APPOINTMENTS, CUSTOMERS, CUTOFF)


def test_columns_and_index(features):
    assert list(features.columns) == FEATURE_COLUMNS
    assert features.index.name == "customer_id"
    assert list(features.index) == ["A", "B", "D", "E"]  # C has a single visit


def test_customer_a_all_features(features):
    a = features.loc["A"]
    avg = 82 / 3
    std = math.sqrt(((20 - avg) ** 2 + (41 - avg) ** 2 + (21 - avg) ** 2) / 3)
    recency = 9 + 14 / 24  # 2025-12-22 10:00 -> 2026-01-01 00:00
    assert a["recency_days"] == pytest.approx(recency)
    assert a["visit_count"] == 4
    assert a["tenure_days"] == pytest.approx(92 - 10 / 24)
    assert a["avg_interval_days"] == pytest.approx(27.33333333)
    assert a["std_interval_days"] == pytest.approx(std)
    assert std == pytest.approx(9.67241, abs=1e-4)
    assert a["recency_ratio"] == pytest.approx(recency / avg)
    assert a["visits_last_90d"] == 3  # window (Oct 3, Jan 1] drops the Oct 1 visit
    assert a["visits_last_180d"] == 4
    assert a["total_spent"] == pytest.approx(150.0)
    assert a["avg_price"] == pytest.approx(37.5)
    assert a["distinct_services"] == 3
    assert a["cancel_count"] == 1
    assert a["no_show_count"] == 1
    assert a["cancel_rate"] == pytest.approx(2 / 7)  # 7 known past appointments
    assert a["has_future_booking"] == 1
    assert a["main_category_Nails"] == 1
    assert a[CATEGORY_COLUMNS].sum() == 1


def test_customer_b_same_day_visits(features):
    b = features.loc["B"]
    assert b["avg_interval_days"] == 1.0  # 0.25 d clipped to 1
    assert b["std_interval_days"] == 0.0  # exactly 2 visits
    assert b["recency_days"] == pytest.approx(30.375)
    assert b["recency_ratio"] == pytest.approx(30.375)
    assert b["tenure_days"] == pytest.approx(30.625)
    assert b["visit_count"] == 2
    assert b["visits_last_90d"] == 2
    assert b["visits_last_180d"] == 2
    assert b["total_spent"] == pytest.approx(50.0)
    assert b["avg_price"] == pytest.approx(25.0)
    assert b["distinct_services"] == 1
    assert b["cancel_count"] == 0
    assert b["no_show_count"] == 0
    assert b["cancel_rate"] == 0.0
    assert b["has_future_booking"] == 0  # one created after cutoff, one cancelled before it
    assert b["main_category_Waxing"] == 1


def test_customer_d_old_customer(features):
    d = features.loc["D"]
    assert d["recency_days"] == pytest.approx(184 - 10 / 24)
    assert d["tenure_days"] == pytest.approx(214 - 10 / 24)
    assert d["avg_interval_days"] == pytest.approx(30)
    assert d["std_interval_days"] == 0
    assert d["recency_ratio"] == pytest.approx((184 - 10 / 24) / 30)
    assert d["visits_last_90d"] == 0
    assert d["visits_last_180d"] == 0  # window (Jul 5, Jan 1] excludes Jul 1
    assert d["total_spent"] == pytest.approx(110.0)
    assert d["has_future_booking"] == 1  # cancelled only after the cutoff
    assert d["cancel_count"] == 0  # future appointment, never counted
    assert d["cancel_rate"] == 0.0
    assert d["main_category_Massage"] == 1


def test_category_tie_is_alphabetical(features):
    e = features.loc["E"]
    assert e["main_category_Nails"] == 1
    assert e["main_category_Skin"] == 0
    assert e[CATEGORY_COLUMNS].sum() == 1


def test_all_six_category_columns_exist(features):
    assert len(CATEGORY_COLUMNS) == 6
    assert set(CATEGORY_COLUMNS) <= set(features.columns)


def test_empty_result_has_right_shape():
    result = build_features(APPOINTMENTS, CUSTOMERS, ts("2025-01-01"))
    assert result.empty
    assert list(result.columns) == FEATURE_COLUMNS
    assert result.index.name == "customer_id"


def test_known_at_does_not_mutate_and_adds_effective_status():
    before = APPOINTMENTS.copy()
    known = known_at(APPOINTMENTS, CUTOFF)
    pd.testing.assert_frame_equal(APPOINTMENTS, before)
    assert "effective_status" in known.columns
    assert "effective_status" not in APPOINTMENTS.columns
    by_id = known.set_index("appointment_id")["effective_status"]
    assert by_id["a7"] == "Scheduled"  # cancelled after cutoff
    assert by_id["a5"] == "Cancelled"
    assert by_id["d3"] == "Scheduled"
    assert "b3" not in by_id.index  # created after the cutoff


def test_known_at_hides_outcome_of_future_appointments():
    rows = frame(
        [
            appt("f1", "A", "2026-01-10T10:00:00Z", status="Completed", created="2025-12-20"),
            appt("f2", "A", "2026-01-11T10:00:00Z", status="NoShow", created="2025-12-20"),
        ]
    )
    known = known_at(rows, CUTOFF)
    assert set(known["effective_status"]) == {"Scheduled"}
