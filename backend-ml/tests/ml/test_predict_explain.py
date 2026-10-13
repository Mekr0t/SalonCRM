import pytest

from app.ml.explain import explain
from app.ml.predict import risk_level


@pytest.mark.parametrize(
    ("p", "expected"),
    [
        (0.0, "low"),
        (0.3299, "low"),
        (0.33, "medium"),
        (0.6599, "medium"),
        (0.66, "high"),
        (1.0, "high"),
    ],
)
def test_risk_level_thresholds(p, expected):
    assert risk_level(p, 0.33, 0.66) == expected


def row(**overrides):
    base = {
        "recency_days": 64.0,
        "avg_interval_days": 21.0,
        "recency_ratio": 64 / 21,
        "has_future_booking": 0.0,
        "cancel_rate": 0.0,
    }
    return {**base, **overrides}


def test_spec_example():
    assert explain(row()) == (
        "Posledná návšteva pred 64 dňami, obvykle chodí každých 21 dní "
        "(3,0× dlhšie ako zvyčajne). Nemá naplánovaný ďalší termín."
    )


@pytest.mark.parametrize(
    ("days", "text"),
    [(1, "pred 1 dňom,"), (2, "pred 2 dňami,"), (4, "pred 4 dňami,"), (5, "pred 5 dňami,"),
     (21, "pred 21 dňami,"), (64, "pred 64 dňami,")],
)  # fmt: skip
def test_recency_plurals(days, text):
    assert text in explain(row(recency_days=float(days)))


@pytest.mark.parametrize(
    ("interval", "text"),
    [(1, "každý deň"), (2, "každé 2 dni"), (4, "každé 4 dni"), (5, "každých 5 dní"),
     (21, "každých 21 dní")],
)  # fmt: skip
def test_interval_plurals(interval, text):
    assert text in explain(row(avg_interval_days=float(interval)))


def test_regular_customer_and_future_booking():
    text = explain(row(recency_days=10.0, recency_ratio=0.5, has_future_booking=1.0))
    assert "Chodí pravidelne, obvykle každých 21 dní." in text
    assert "chodí pravidelne" not in text
    assert "dlhšie ako zvyčajne" not in text
    assert "Má naplánovaný ďalší termín." in text
    assert "Nemá" not in text


def test_rounding_and_cancel_sentence():
    text = explain(row(recency_days=63.6, cancel_rate=0.25))
    assert "pred 64 dňami" in text
    assert "(25 % termínov)" in text
    assert "ruší" in text
    assert "ruší" not in explain(row(cancel_rate=0.19))


@pytest.mark.parametrize(
    ("ratio", "mentioned"), [(1.0, False), (1.04, False), (1.1, True), (2.5, True)]
)
def test_ratio_mentioned_only_from_1_1(ratio, mentioned):
    text = explain(row(recency_ratio=ratio))
    assert ("dlhšie ako zvyčajne" in text) is mentioned
    assert "1,0×" not in text
