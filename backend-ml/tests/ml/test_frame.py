import copy

import pandas as pd
import pytest

from app.ml.frame import APPOINTMENT_COLUMNS, CUSTOMER_COLUMNS, from_export

PAYLOAD = {
    "generatedAt": "2026-10-12T16:00:00Z",
    "customers": [{"customerId": "c1", "createdAt": "2025-01-14T09:12:00Z"}],
    "appointments": [
        {
            "appointmentId": "a1",
            "customerId": "c1",
            "employeeId": "e1",
            "serviceId": "s1",
            "serviceCategory": "Nails",
            "startsAt": "2026-09-30T10:00:00+02:00",
            "createdAt": "2026-09-18T17:40:00Z",
            "cancelledAt": None,
            "status": "Completed",
            "price": 30,
        },
        {
            "appointmentId": "a2",
            "customerId": "c1",
            "employeeId": "e1",
            "serviceId": "s1",
            "serviceCategory": "Nails",
            "startsAt": "2026-10-05T10:00:00Z",
            "createdAt": "2026-09-20T08:00:00+02:00",
            "cancelledAt": "2026-09-25T08:00:00Z",
            "status": "Cancelled",
            "price": 30.5,
        },
    ],
}


def payload():
    return copy.deepcopy(PAYLOAD)


def test_round_trip():
    customers, appts = from_export(payload())
    assert list(customers.columns) == CUSTOMER_COLUMNS
    assert list(appts.columns) == APPOINTMENT_COLUMNS
    assert customers.loc[0, "customer_id"] == "c1"
    assert appts.loc[0, "service_category"] == "Nails"
    assert appts.loc[0, "status"] == "Completed"


def test_offsets_converted_to_utc():
    _, appts = from_export(payload())
    assert str(appts["starts_at"].dt.tz) == "UTC"
    assert appts.loc[0, "starts_at"] == pd.Timestamp("2026-09-30T08:00:00Z")
    assert appts.loc[1, "created_at"] == pd.Timestamp("2026-09-20T06:00:00Z")


def test_cancelled_at_null_and_absent_become_nat():
    _, appts = from_export(payload())
    assert pd.isna(appts.loc[0, "cancelled_at"])
    assert appts.loc[1, "cancelled_at"] == pd.Timestamp("2026-09-25T08:00:00Z")
    p = payload()
    for a in p["appointments"]:
        a.pop("cancelledAt")
    _, appts = from_export(p)
    assert appts["cancelled_at"].isna().all()
    assert str(appts["cancelled_at"].dt.tz) == "UTC"


def test_price_is_float():
    _, appts = from_export(payload())
    assert appts["price"].dtype == float
    assert appts.loc[0, "price"] == 30.0


@pytest.mark.parametrize("bad", [None, "abc"])
def test_bad_price_rejected(bad):
    p = payload()
    p["appointments"][0]["price"] = bad
    with pytest.raises(ValueError, match="price"):
        from_export(p)


def test_missing_price_rejected():
    p = payload()
    del p["appointments"][0]["price"]
    with pytest.raises(ValueError, match="price"):
        from_export(p)


@pytest.mark.parametrize("field", ["startsAt", "serviceId", "status"])
def test_missing_required_field(field):
    p = payload()
    for a in p["appointments"]:
        del a[field]
    with pytest.raises(ValueError, match="missing required field"):
        from_export(p)


def test_missing_customer_field():
    p = payload()
    del p["customers"][0]["createdAt"]
    with pytest.raises(ValueError, match="created_at"):
        from_export(p)


@pytest.mark.parametrize("key", ["customers", "appointments"])
def test_missing_top_level_key(key):
    p = payload()
    del p[key]
    with pytest.raises(ValueError, match=key):
        from_export(p)


def test_bad_datetime_names_field():
    p = payload()
    p["appointments"][0]["startsAt"] = "not a date"
    with pytest.raises(ValueError, match="starts_at"):
        from_export(p)


def test_empty_lists_give_empty_frames():
    customers, appts = from_export({"customers": [], "appointments": []})
    assert customers.empty and appts.empty
    assert list(customers.columns) == CUSTOMER_COLUMNS
    assert list(appts.columns) == APPOINTMENT_COLUMNS
