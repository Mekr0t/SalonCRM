"""Feature engineering (SPEC §8.4). Every feature is computed as of a cutoff date T."""

import pandas as pd

from app.ml.frame import CATEGORIES, DAY

CATEGORY_COLUMNS = [f"main_category_{c}" for c in CATEGORIES]
FEATURE_COLUMNS = [
    "recency_days",
    "visit_count",
    "tenure_days",
    "avg_interval_days",
    "std_interval_days",
    "recency_ratio",
    "visits_last_90d",
    "visits_last_180d",
    "total_spent",
    "avg_price",
    "distinct_services",
    "cancel_count",
    "no_show_count",
    "cancel_rate",
    "has_future_booking",
    *CATEGORY_COLUMNS,
]
MIN_AVG_INTERVAL_DAYS = 1.0


def known_at(appointments: pd.DataFrame, cutoff: pd.Timestamp) -> pd.DataFrame:
    """State of the appointments as it was known at `cutoff` (SPEC §8.4). Input is not mutated.

    - created after the cutoff -> did not exist yet, dropped
    - Cancelled after the cutoff -> not cancelled yet (effective status Scheduled)
    - Completed/NoShow with starts_at after the cutoff -> outcome not known yet (Scheduled)
    """
    df = appointments[appointments["created_at"] <= cutoff].copy()
    status = df["status"].astype(object)
    cancelled_later = (status == "Cancelled") & (df["cancelled_at"] > cutoff)
    # SPEC §8.4: Completed/NoShow only count once starts_at <= T; make that explicit here.
    outcome_unknown = status.isin(["Completed", "NoShow"]) & (df["starts_at"] > cutoff)
    df["effective_status"] = status.mask(cancelled_later | outcome_unknown, "Scheduled")
    return df


def _empty() -> pd.DataFrame:
    empty = pd.DataFrame({c: pd.Series(dtype=float) for c in FEATURE_COLUMNS})
    empty.index.name = "customer_id"
    return empty


def _count_by_customer(df: pd.DataFrame, index: pd.Index) -> pd.Series:
    return df.groupby("customer_id").size().reindex(index, fill_value=0).astype(float)


def _interval_stats(completed: pd.DataFrame, index: pd.Index) -> pd.DataFrame:
    """Mean and population std of gaps (days) between consecutive visits per customer."""
    gaps = completed.groupby("customer_id")["starts_at"].diff().dt.total_seconds() / 86400
    gaps = gaps.dropna()
    grouped = gaps.groupby(completed.loc[gaps.index, "customer_id"])
    return pd.DataFrame({"avg": grouped.mean(), "std": grouped.std(ddof=0)}).reindex(index)


def _main_category(completed: pd.DataFrame, index: pd.Index) -> pd.DataFrame:
    counts = completed.groupby(["customer_id", "service_category"]).size().rename("n")
    counts = counts.reset_index().sort_values(
        ["customer_id", "n", "service_category"], ascending=[True, False, True]
    )
    top = counts.drop_duplicates("customer_id").set_index("customer_id")["service_category"]
    top = top.reindex(index)
    return pd.DataFrame(
        {f"main_category_{c}": (top == c).astype(float) for c in CATEGORIES}, index=index
    )


def build_features(
    appointments: pd.DataFrame, customers: pd.DataFrame, cutoff: pd.Timestamp
) -> pd.DataFrame:
    """Features per customer with >= 2 Completed visits at `cutoff`, indexed by customer_id.

    Leakage guard: `known_at` runs first and everything below reads only its output.
    """
    known = known_at(appointments, cutoff)
    known = known[known["customer_id"].isin(customers["customer_id"])]
    past = known[known["starts_at"] <= cutoff]
    completed = past[past["effective_status"] == "Completed"]
    completed = completed.sort_values(["customer_id", "starts_at"])
    visit_counts = completed.groupby("customer_id").size()
    index = pd.Index(sorted(visit_counts[visit_counts >= 2].index), name="customer_id")
    if len(index) == 0:
        return _empty()
    completed = completed[completed["customer_id"].isin(index)]

    by_customer = completed.groupby("customer_id")
    out = pd.DataFrame(index=index)
    out["recency_days"] = (cutoff - by_customer["starts_at"].max()) / DAY
    out["visit_count"] = visit_counts.reindex(index).astype(float)
    out["tenure_days"] = (cutoff - by_customer["starts_at"].min()) / DAY
    intervals = _interval_stats(completed, index)
    out["avg_interval_days"] = intervals["avg"].clip(lower=MIN_AVG_INTERVAL_DAYS)
    out["std_interval_days"] = intervals["std"]
    out["recency_ratio"] = out["recency_days"] / out["avg_interval_days"]
    for days in (90, 180):
        window = completed[completed["starts_at"] > cutoff - days * DAY]
        out[f"visits_last_{days}d"] = _count_by_customer(window, index)
    out["total_spent"] = by_customer["price"].sum()
    out["avg_price"] = by_customer["price"].mean()
    out["distinct_services"] = by_customer["service_id"].nunique().astype(float)

    past = past[past["customer_id"].isin(index)]
    out["cancel_count"] = _count_by_customer(past[past["effective_status"] == "Cancelled"], index)
    out["no_show_count"] = _count_by_customer(past[past["effective_status"] == "NoShow"], index)
    out["cancel_rate"] = (out["cancel_count"] + out["no_show_count"]) / _count_by_customer(
        past, index
    )
    future = known[(known["starts_at"] > cutoff) & (known["effective_status"] != "Cancelled")]
    out["has_future_booking"] = index.isin(future["customer_id"]).astype(float)
    out = out.join(_main_category(completed, index))
    return out[FEATURE_COLUMNS].astype(float)
