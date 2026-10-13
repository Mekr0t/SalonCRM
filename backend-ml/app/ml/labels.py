"""Snapshot dates, churn labels and the training dataset (SPEC §8.3)."""

import pandas as pd

from app.ml.features import build_features
from app.ml.frame import DAY


def snapshot_dates(
    reference_date: pd.Timestamp, horizon_days: int, count: int, step_days: int
) -> list[pd.Timestamp]:
    """T_k = R - H - k*step for k = 0..count-1 (list index is k)."""
    return [reference_date - (horizon_days + k * step_days) * DAY for k in range(count)]


def build_labels(
    appointments: pd.DataFrame,
    customer_ids: pd.Index,
    cutoff: pd.Timestamp,
    horizon_days: int,
) -> pd.Series:
    """y = 1 if the customer has no Completed visit with starts_at in (cutoff, cutoff + H].

    Labels deliberately use the final status: the window lies in the past relative to R.
    """
    window = appointments[
        (appointments["status"] == "Completed")
        & (appointments["starts_at"] > cutoff)
        & (appointments["starts_at"] <= cutoff + horizon_days * DAY)
    ]
    returned = pd.Index(window["customer_id"].unique())
    y = ~pd.Index(customer_ids).isin(returned)
    return pd.Series(y.astype(int), index=pd.Index(customer_ids, name="customer_id"), name="y")


def build_dataset(
    appointments: pd.DataFrame,
    customers: pd.DataFrame,
    reference_date: pd.Timestamp,
    horizon_days: int = 90,
    count: int = 8,
    step_days: int = 30,
) -> pd.DataFrame:
    """One block of rows per snapshot: snapshot_k, cutoff, customer_id, features..., y."""
    blocks = []
    for k, cutoff in enumerate(snapshot_dates(reference_date, horizon_days, count, step_days)):
        features = build_features(appointments, customers, cutoff)
        block = features.reset_index()
        block.insert(0, "snapshot_k", k)
        block.insert(1, "cutoff", cutoff)
        labels = build_labels(appointments, features.index, cutoff, horizon_days)
        block["y"] = labels.to_numpy()
        blocks.append(block)
    return pd.concat(blocks, ignore_index=True)
