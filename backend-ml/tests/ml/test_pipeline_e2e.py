import pandas as pd

from app.ml.explain import explain
from app.ml.features import FEATURE_COLUMNS, build_features
from app.ml.labels import build_dataset
from app.ml.predict import predict_proba, risk_level
from app.ml.train import train
from tests.ml.synthetic import make_dataset

REF = pd.Timestamp("2026-10-12T12:00:00Z")


def test_end_to_end_on_synthetic_data():
    customers, appts = make_dataset(300, REF, seed=42)
    dataset = build_dataset(appts, customers, REF)
    result = train(dataset, n_repeats=5)

    print(
        f"\nwinner={result.algorithm} auc={result.metrics['roc_auc']:.3f} "
        f"baseline_auc={result.baseline_metrics['roc_auc']:.3f} "
        f"positive_rate={result.positive_rate:.3f} "
        f"n_train={result.n_train} n_valid={result.n_valid}"
    )
    print({n: round(m["roc_auc"], 3) for n, m in result.candidates.items()})
    print(list(result.feature_importances.items())[:5])

    assert result.metrics["roc_auc"] >= 0.6
    assert len(result.candidates) == 3
    for metrics in [*result.candidates.values(), result.baseline_metrics]:
        assert {"roc_auc", "precision", "recall", "f1", "confusion_matrix"} <= set(metrics)
    assert set(result.feature_importances) == set(FEATURE_COLUMNS)

    features = build_features(appts, customers, REF)
    assert len(features) > 50
    proba = predict_proba(result.model, features)
    assert proba.between(0, 1).all()
    assert list(proba.index) == list(features.index)
    levels = {risk_level(p) for p in proba}
    assert levels <= {"low", "medium", "high"}
    assert explain(features.iloc[0].to_dict())
