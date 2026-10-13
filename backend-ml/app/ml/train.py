"""Model training, evaluation and persistence (SPEC §8.3 steps 4-8)."""

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import joblib
import pandas as pd
from sklearn.base import BaseEstimator, clone
from sklearn.ensemble import HistGradientBoostingClassifier, RandomForestClassifier
from sklearn.inspection import permutation_importance
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (
    confusion_matrix,
    f1_score,
    precision_score,
    recall_score,
    roc_auc_score,
)
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

from app.ml.features import FEATURE_COLUMNS

BASELINE_THRESHOLD = 2.0
MODEL_THRESHOLD = 0.5


@dataclass
class TrainingResult:
    algorithm: str
    params: dict[str, Any]
    model: Any
    metrics: dict[str, Any]
    baseline_metrics: dict[str, Any]
    candidates: dict[str, dict[str, Any]]
    feature_importances: dict[str, float]
    n_train: int
    n_valid: int
    positive_rate: float
    warnings: list[str] = field(default_factory=list)


def split_by_time(
    dataset: pd.DataFrame, gap: int = 3, horizon_days: int = 90
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Validation = newest snapshot (k = 0); training = k >= gap.

    Raises ValueError unless T_k + H <= T_0 for every training snapshot, i.e. no label window
    reaches into the validation period (SPEC §8.3 step 4).
    """
    if gap < 1:
        raise ValueError("validation gap must be at least 1 snapshot")
    valid = dataset[dataset["snapshot_k"] == 0]
    train = dataset[dataset["snapshot_k"] >= gap]
    if not valid.empty and not train.empty:
        t0 = valid["cutoff"].iloc[0]
        latest_label_end = train["cutoff"].max() + pd.Timedelta(days=horizon_days)
        if latest_label_end > t0:
            raise ValueError("training label window overlaps the validation period")
    return train, valid


def candidate_models(random_state: int) -> dict[str, BaseEstimator]:
    return {
        "logistic_regression": Pipeline(
            [
                ("scaler", StandardScaler()),
                (
                    "clf",
                    LogisticRegression(
                        class_weight="balanced", max_iter=2000, random_state=random_state
                    ),
                ),
            ]
        ),
        "random_forest": RandomForestClassifier(
            n_estimators=200,
            min_samples_leaf=3,
            class_weight="balanced",
            random_state=random_state,
            n_jobs=1,
        ),
        "hist_gradient_boosting": HistGradientBoostingClassifier(random_state=random_state),
    }


def _metrics(
    y_true: pd.Series, score: pd.Series, y_pred: pd.Series, warnings: list[str], name: str
):
    both_classes = y_true.nunique() == 2
    auc = float(roc_auc_score(y_true, score)) if both_classes else None
    if not both_classes:
        warnings.append(f"{name}: validation set has a single class, ROC-AUC is undefined")
    return {
        "roc_auc": auc,
        "precision": float(precision_score(y_true, y_pred, zero_division=0)),
        "recall": float(recall_score(y_true, y_pred, zero_division=0)),
        "f1": float(f1_score(y_true, y_pred, zero_division=0)),
        "confusion_matrix": confusion_matrix(y_true, y_pred, labels=[0, 1]).tolist(),
        "positive_rate": float(y_true.mean()),
        "n": int(len(y_true)),
    }


def baseline_metrics(valid: pd.DataFrame, warnings: list[str]) -> dict[str, Any]:
    """AUC from the continuous recency_ratio; the other metrics from `ratio > 2.0`."""
    ratio = valid["recency_ratio"]
    return _metrics(
        valid["y"], ratio, (ratio > BASELINE_THRESHOLD).astype(int), warnings, "baseline"
    )


def _model_metrics(model, valid: pd.DataFrame, warnings: list[str], name: str):
    proba = pd.Series(model.predict_proba(valid[FEATURE_COLUMNS])[:, 1], index=valid.index)
    return _metrics(valid["y"], proba, (proba >= MODEL_THRESHOLD).astype(int), warnings, name)


def _json_params(model: BaseEstimator) -> dict[str, Any]:
    estimator = model.named_steps["clf"] if isinstance(model, Pipeline) else model
    return {
        k: (v if isinstance(v, str | int | float | bool | None) else str(v))
        for k, v in estimator.get_params(deep=False).items()
    }


def _importances(model, valid: pd.DataFrame, random_state: int, n_repeats: int) -> dict[str, float]:
    scoring = "roc_auc" if valid["y"].nunique() == 2 else "accuracy"
    result = permutation_importance(
        model,
        valid[FEATURE_COLUMNS],
        valid["y"],
        scoring=scoring,
        n_repeats=n_repeats,
        random_state=random_state,
    )
    pairs = sorted(zip(FEATURE_COLUMNS, result.importances_mean, strict=True), key=lambda p: -p[1])
    return {name: float(value) for name, value in pairs}


def train(
    dataset: pd.DataFrame,
    gap: int = 3,
    horizon_days: int = 90,
    random_state: int = 42,
    n_repeats: int = 10,
) -> TrainingResult:
    train_df, valid_df = split_by_time(dataset, gap, horizon_days)
    if train_df.empty or valid_df.empty:
        raise ValueError("Not enough history: training or validation set is empty")
    if train_df["y"].nunique() < 2:
        raise ValueError("Training set contains a single class; cannot fit a classifier")

    warnings: list[str] = []
    baseline = baseline_metrics(valid_df, warnings)
    fitted: dict[str, BaseEstimator] = {}
    candidates: dict[str, dict[str, Any]] = {}
    for name, model in candidate_models(random_state).items():
        model.fit(train_df[FEATURE_COLUMNS], train_df["y"])
        fitted[name] = model
        candidates[name] = _model_metrics(model, valid_df, warnings, name)

    # Ties and undefined AUC fall back to the first candidate (stable, deterministic order).
    winner = max(candidates, key=lambda n: candidates[n]["roc_auc"] or -1.0)
    importances = _importances(fitted[winner], valid_df, random_state, n_repeats)

    final = clone(fitted[winner])
    final.fit(dataset[FEATURE_COLUMNS], dataset["y"])
    return TrainingResult(
        algorithm=winner,
        params=_json_params(final),
        model=final,
        metrics=candidates[winner],
        baseline_metrics=baseline,
        candidates=candidates,
        feature_importances=importances,
        n_train=len(train_df),
        n_valid=len(valid_df),
        positive_rate=float(dataset["y"].mean()),
        warnings=warnings,
    )


def save_model(model: Any, path: str | Path, algorithm: str = "") -> None:
    payload = {"model": model, "feature_columns": list(FEATURE_COLUMNS), "algorithm": algorithm}
    joblib.dump(payload, path)


def load_model(path: str | Path) -> Any:
    payload = joblib.load(path)
    if not isinstance(payload, dict) or payload.get("feature_columns") != FEATURE_COLUMNS:
        raise ValueError("Model file does not match the current FEATURE_COLUMNS; retrain the model")
    return payload["model"]
