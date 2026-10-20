import numpy as np
import pandas as pd
import pytest

from app.ml.features import FEATURE_COLUMNS
from app.ml.labels import build_dataset
from app.ml.predict import predict_proba
from app.ml.train import (
    InsufficientDataError,
    baseline_metrics,
    load_model,
    save_model,
    split_by_time,
    train,
)
from tests.ml.synthetic import make_dataset

REF = pd.Timestamp("2026-10-12T12:00:00Z")


@pytest.fixture(scope="module")
def dataset():
    customers, appts = make_dataset(150, REF, seed=7)
    return build_dataset(appts, customers, REF)


def test_dataset_shape(dataset):
    assert sorted(dataset["snapshot_k"].unique()) == list(range(8))
    assert list(dataset.columns) == ["snapshot_k", "cutoff", "customer_id", *FEATURE_COLUMNS, "y"]
    assert set(dataset["y"].unique()) <= {0, 1}


def test_split_has_gap(dataset):
    train_df, valid_df = split_by_time(dataset, gap=3)
    assert set(valid_df["snapshot_k"]) == {0}
    assert set(train_df["snapshot_k"]) == {3, 4, 5, 6, 7}
    t0 = valid_df["cutoff"].iloc[0]
    assert (train_df["cutoff"] + pd.Timedelta(days=90) <= t0).all()


def test_training_is_deterministic(dataset):
    a = train(dataset, n_repeats=3)
    b = train(dataset, n_repeats=3)
    assert a.algorithm == b.algorithm
    assert a.metrics == b.metrics
    assert a.baseline_metrics == b.baseline_metrics
    assert a.candidates == b.candidates
    assert a.feature_importances == b.feature_importances


def test_baseline_uses_continuous_recency_ratio(dataset):
    _, valid = split_by_time(dataset, gap=3)
    from sklearn.metrics import roc_auc_score

    metrics = baseline_metrics(valid, [])
    assert metrics["roc_auc"] == pytest.approx(roc_auc_score(valid["y"], valid["recency_ratio"]))
    predicted = valid["recency_ratio"] > 2.0
    tp = int((predicted & (valid["y"] == 1)).sum())
    assert metrics["confusion_matrix"][1][1] == tp


def test_result_contents(dataset):
    result = train(dataset, n_repeats=3)
    assert set(result.candidates) == {
        "logistic_regression",
        "random_forest",
        "hist_gradient_boosting",
    }
    assert list(result.feature_importances) != []
    assert set(result.feature_importances) == set(FEATURE_COLUMNS)
    values = list(result.feature_importances.values())
    assert values == sorted(values, reverse=True)
    assert result.n_valid == int((dataset["snapshot_k"] == 0).sum())


def test_single_class_validation_does_not_crash(dataset):
    broken = dataset.copy()
    broken.loc[broken["snapshot_k"] == 0, "y"] = 0
    result = train(broken, n_repeats=2)
    assert result.metrics["roc_auc"] is None
    assert result.baseline_metrics["roc_auc"] is None
    assert any("single class" in w for w in result.warnings)


def test_save_load_round_trip(dataset, tmp_path):
    result = train(dataset, n_repeats=2)
    path = tmp_path / "model.joblib"
    save_model(result.model, path, result.algorithm)
    loaded = load_model(path)
    features = dataset[FEATURE_COLUMNS].head(30)
    np.testing.assert_array_equal(
        predict_proba(result.model, features).to_numpy(),
        predict_proba(loaded, features).to_numpy(),
    )


def test_load_rejects_mismatched_columns(tmp_path):
    import joblib

    path = tmp_path / "bad.joblib"
    joblib.dump({"model": object(), "feature_columns": ["x"]}, path)
    with pytest.raises(ValueError):
        load_model(path)


def test_split_gap_violation_raises_value_error(dataset):
    with pytest.raises(ValueError, match="overlaps"):
        split_by_time(dataset, gap=1, horizon_days=90)
    with pytest.raises(ValueError):
        split_by_time(dataset, gap=0)


def test_data_checks_raise_insufficient_data_error(dataset):
    assert issubclass(InsufficientDataError, ValueError)
    without_validation = dataset[dataset["snapshot_k"] != 0]
    with pytest.raises(InsufficientDataError, match="Not enough history"):
        train(without_validation, n_repeats=2)
    single_class = dataset.copy()
    single_class.loc[single_class["snapshot_k"] >= 3, "y"] = 0
    with pytest.raises(InsufficientDataError, match="single class"):
        train(single_class, n_repeats=2)
