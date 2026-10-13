"""Scoring and risk levels (SPEC §8.5)."""

import pandas as pd

from app.ml.features import FEATURE_COLUMNS


def predict_proba(model, features: pd.DataFrame) -> pd.Series:
    """Churn probability per row of `features` (same index)."""
    if features.empty:
        return pd.Series(dtype=float, index=features.index, name="churn_probability")
    proba = model.predict_proba(features[FEATURE_COLUMNS])[:, 1]
    return pd.Series(proba, index=features.index, name="churn_probability")


def risk_level(p: float, medium: float = 0.33, high: float = 0.66) -> str:
    """p < medium -> low, medium <= p < high -> medium, p >= high -> high."""
    if p >= high:
        return "high"
    if p >= medium:
        return "medium"
    return "low"
