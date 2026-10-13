"""Deterministic synthetic salon history (test helper, not a test)."""

import numpy as np
import pandas as pd

DAY = pd.Timedelta(days=1)
HISTORY_DAYS = 730
SERVICES = [
    ("s1", "Skin", 45.0),
    ("s2", "Skin", 60.0),
    ("s3", "Nails", 30.0),
    ("s4", "Nails", 35.0),
    ("s5", "BrowsLashes", 25.0),
    ("s6", "BrowsLashes", 40.0),
    ("s7", "Waxing", 20.0),
    ("s8", "Massage", 55.0),
    ("s9", "Makeup", 50.0),
]
ARCHETYPES = ["loyal", "irregular", "churning", "one_off"]
ARCHETYPE_WEIGHTS = [0.3, 0.25, 0.3, 0.15]
STATUS_PROBS = {"default": [0.90, 0.06, 0.04], "churning": [0.78, 0.14, 0.08]}


def _visit_times(kind: str, ref: pd.Timestamp, rng: np.random.Generator) -> list[pd.Timestamp]:
    first_back = rng.uniform(150, HISTORY_DAYS) if kind != "one_off" else rng.uniform(0, 700)
    t = ref - first_back * DAY
    times = [t]
    if kind == "one_off":
        if rng.random() < 0.5:
            times.append(t + rng.uniform(20, 120) * DAY)
        return times
    stop = ref + 25 * DAY
    if kind == "churning":
        stop = ref - rng.uniform(90, 450) * DAY
        if stop < t + 60 * DAY:
            stop = t + rng.uniform(60, 200) * DAY
    base = rng.uniform(20, 45) if kind != "irregular" else None
    while True:
        gap = rng.uniform(60, 120) if base is None else base * rng.uniform(0.85, 1.15)
        t = t + gap * DAY
        if t > stop:
            return times
        if kind == "loyal" and t <= ref and rng.random() < 0.1:
            continue  # occasionally skipped appointment
        times.append(t)


def make_dataset(
    n_customers: int, reference_date: pd.Timestamp, seed: int
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Return (customers, appointments) in the shape produced by `from_export`."""
    rng = np.random.default_rng(seed)
    customers, rows = [], []
    for c in range(n_customers):
        kind = str(rng.choice(ARCHETYPES, p=ARCHETYPE_WEIGHTS))
        cid = f"c{c:04d}"
        favourite = SERVICES[int(rng.integers(len(SERVICES)))]
        probs = STATUS_PROBS["churning" if kind == "churning" else "default"]
        first_created = None
        for i, start in enumerate(_visit_times(kind, reference_date, rng)):
            sid, category, price = (
                favourite if rng.random() < 0.6 else SERVICES[int(rng.integers(9))]
            )
            created = min(start - rng.uniform(1, 21) * DAY, reference_date - DAY)
            if start > reference_date:
                status = "Scheduled"
            else:
                status = str(rng.choice(["Completed", "Cancelled", "NoShow"], p=probs))
            if i == 0 and status != "Completed":
                status = "Completed"
            cancelled = None
            if status == "Cancelled":
                cancelled = created + rng.uniform(0, 1) * (start - created)
            if first_created is None:
                first_created = created
            rows.append(
                {
                    "appointment_id": f"a{c:04d}_{i:03d}",
                    "customer_id": cid,
                    "employee_id": f"e{int(rng.integers(3))}",
                    "service_id": sid,
                    "service_category": category,
                    "starts_at": start,
                    "created_at": created,
                    "cancelled_at": cancelled,
                    "status": status,
                    "price": price,
                }
            )
        customers.append({"customer_id": cid, "created_at": first_created - DAY})
    appts = pd.DataFrame(rows)
    for col in ("starts_at", "created_at", "cancelled_at"):
        appts[col] = pd.to_datetime(appts[col], utc=True)
    return pd.DataFrame(customers), appts
