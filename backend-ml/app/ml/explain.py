"""Slovak, human-readable explanation of a churn score (SPEC §8.5)."""

from collections.abc import Mapping

CANCEL_RATE_NOTE_THRESHOLD = 0.2


def _num(value: float) -> str:
    return f"{value:.1f}".replace(".", ",")


def days_instrumental(n: int) -> str:
    """'1 dňom' / 'N dňami' (used after 'pred')."""
    return "1 dňom" if n == 1 else f"{n} dňami"


def every_interval(n: int) -> str:
    """'každý deň' / 'každé N dni' (2-4) / 'každých N dní' (5+)."""
    if n <= 1:
        return "každý deň"
    if n <= 4:
        return f"každé {n} dni"
    return f"každých {n} dní"


MIN_MENTIONED_RATIO = 1.1


def _last_visit_sentence(row: Mapping[str, float]) -> str:
    recency = int(round(row["recency_days"]))
    interval = max(int(round(row["avg_interval_days"])), 1)
    ratio = round(float(row["recency_ratio"]), 1)
    if recency == 0:
        since = "Posledná návšteva bola dnes"
    else:
        since = f"Posledná návšteva pred {days_instrumental(recency)}"
    if ratio < MIN_MENTIONED_RATIO:
        return f"{since}. Chodí pravidelne, obvykle {every_interval(interval)}."
    usual = f"obvykle chodí {every_interval(interval)}"
    return f"{since}, {usual} ({_num(ratio)}× dlhšie ako zvyčajne)."


def explain(features_row: Mapping[str, float]) -> str:
    parts = [_last_visit_sentence(features_row)]
    if features_row["has_future_booking"] >= 0.5:
        parts.append("Má naplánovaný ďalší termín.")
    else:
        parts.append("Nemá naplánovaný ďalší termín.")
    rate = features_row["cancel_rate"]
    if rate >= CANCEL_RATE_NOTE_THRESHOLD:
        parts.append(f"Často ruší alebo nechodí na termíny ({round(rate * 100)} % termínov).")
    return " ".join(parts)
