"""Conversion of the main-backend export (SPEC §7) into pandas frames."""

import pandas as pd

DAY = pd.Timedelta(days=1)

CUSTOMER_COLUMNS = ["customer_id", "created_at"]
APPOINTMENT_COLUMNS = [
    "appointment_id",
    "customer_id",
    "employee_id",
    "service_id",
    "service_category",
    "starts_at",
    "created_at",
    "cancelled_at",
    "status",
    "price",
]
CATEGORIES = ["BrowsLashes", "Makeup", "Massage", "Nails", "Skin", "Waxing"]

_CAMEL_TO_SNAKE = {
    "customerId": "customer_id",
    "createdAt": "created_at",
    "appointmentId": "appointment_id",
    "employeeId": "employee_id",
    "serviceId": "service_id",
    "serviceCategory": "service_category",
    "startsAt": "starts_at",
    "cancelledAt": "cancelled_at",
}
_TIME_COLUMNS = ["starts_at", "created_at", "cancelled_at"]


OPTIONAL_COLUMNS = {"cancelled_at"}


def _parse_times(df: pd.DataFrame, name: str) -> None:
    for column in _TIME_COLUMNS:
        if column not in df.columns:
            continue
        try:
            df[column] = pd.to_datetime(df[column], utc=True, format="ISO8601")
        except (ValueError, TypeError) as exc:
            raise ValueError(
                f"Export '{name}': invalid datetime in field '{column}': {exc}"
            ) from exc


def _to_frame(rows: list[dict], columns: list[str], name: str) -> pd.DataFrame:
    df = pd.DataFrame(rows).rename(columns=_CAMEL_TO_SNAKE)
    for column in columns:
        if column not in df.columns:
            if column not in OPTIONAL_COLUMNS and len(df) > 0:
                raise ValueError(f"Export '{name}' is missing required field '{column}'")
            df[column] = None
    df = df[columns].copy()
    _parse_times(df, name)
    return df


def from_export(payload: dict) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Return (customers, appointments) with snake_case columns and UTC timestamps.

    `cancelledAt` may be null or absent; all other fields are required.
    """
    for key in ("customers", "appointments"):
        if not isinstance(payload.get(key), list):
            raise ValueError(f"Export payload must contain a list '{key}'")
    customers = _to_frame(payload["customers"], CUSTOMER_COLUMNS, "customers")
    appointments = _to_frame(payload["appointments"], APPOINTMENT_COLUMNS, "appointments")
    try:
        appointments["price"] = pd.to_numeric(appointments["price"]).astype(float)
    except (ValueError, TypeError) as exc:
        raise ValueError(f"Export 'appointments': invalid value in field 'price': {exc}") from exc
    if appointments["price"].isna().any():
        raise ValueError("Export 'appointments': field 'price' must not be null")
    return customers, appointments
