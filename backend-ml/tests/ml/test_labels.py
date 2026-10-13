import pandas as pd

from app.ml.labels import build_labels, snapshot_dates

CUTOFF = pd.Timestamp("2026-01-01T00:00:00Z")
DAY = pd.Timedelta(days=1)


def _appts(rows):
    df = pd.DataFrame(rows, columns=["customer_id", "starts_at", "status"])
    df["starts_at"] = pd.to_datetime(df["starts_at"], utc=True)
    return df


def test_label_boundaries():
    appts = _appts(
        [
            ("at_cutoff", CUTOFF, "Completed"),  # window is open on the left -> not counted
            ("at_end", CUTOFF + 90 * DAY, "Completed"),  # closed on the right -> counted
            ("after_end", CUTOFF + 90 * DAY + pd.Timedelta(seconds=1), "Completed"),
            ("cancelled", CUTOFF + 10 * DAY, "Cancelled"),
            ("no_show", CUTOFF + 10 * DAY, "NoShow"),
            ("inside", CUTOFF + 10 * DAY, "Completed"),
        ]
    )
    ids = pd.Index(["at_cutoff", "at_end", "after_end", "cancelled", "no_show", "inside", "none"])
    y = build_labels(appts, ids, CUTOFF, 90)
    assert y.to_dict() == {
        "at_cutoff": 1,
        "at_end": 0,
        "after_end": 1,
        "cancelled": 1,
        "no_show": 1,
        "inside": 0,
        "none": 1,
    }


def test_snapshot_dates_fixed_reference():
    ref = pd.Timestamp("2026-10-12T00:00:00Z")
    dates = snapshot_dates(ref, 90, 8, 30)
    assert len(dates) == 8
    assert dates[0] == pd.Timestamp("2026-07-14T00:00:00Z")
    assert dates[1] == pd.Timestamp("2026-06-14T00:00:00Z")
    assert dates[7] == ref - 300 * DAY
    assert all(a - b == 30 * DAY for a, b in zip(dates, dates[1:], strict=False))
