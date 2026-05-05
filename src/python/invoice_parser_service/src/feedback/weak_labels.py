"""Infer per-word labels from corrected field strings + OCR words (weak supervision)."""

from __future__ import annotations

import re
from typing import Optional


def _norm(s: str) -> str:
    return re.sub(r"\s+", " ", s.strip().lower())


def _first_non_empty(d: dict[str, Optional[str]], keys: tuple[str, ...]) -> Optional[str]:
    for k in keys:
        v = d.get(k)
        if v is None:
            continue
        t = str(v).strip()
        if t:
            return t
    return None


def _canonical_for_weak_labels(corrected_fields: dict[str, Optional[str]]) -> dict[str, str]:
    """
    Map arbitrary C# / API keys to the four LayoutLM field ids used for span matching.
    Full corrected_fields is still stored on the dataset row; this only drives weak labels.
    """
    cf = corrected_fields
    out: dict[str, str] = {}
    inv = _first_non_empty(cf, ("invoice_no", "invoice_number"))
    if inv:
        out["invoice_no"] = inv
    dt = _first_non_empty(
        cf,
        ("date", "invoice_date", "invoice_st_dtm", "invoice_end_dtm", "invoice_due_dtm"),
    )
    if dt:
        out["date"] = dt
    total = _first_non_empty(
        cf,
        (
            "total",
            "end_bal",
            "curr_chg",
            "beg_bal",
            "payment",
            "curr_tax",
            "prev_adj",
            "curr_adj",
        ),
    )
    if total:
        out["total"] = total
    vend = _first_non_empty(cf, ("vendor", "carrier_name"))
    if vend:
        out["vendor"] = vend
    if "total" not in out:
        acct = _first_non_empty(cf, ("carrier_account", "account"))
        if acct:
            # Best-effort span when no monetary total was provided (no ACCOUNT label in head).
            out["total"] = acct
    return out


def assign_word_labels_from_corrections(
    words: list[str],
    bboxes: list[list[float]],
    corrected_fields: dict[str, Optional[str]],
) -> list[str]:
    """
    Map corrected values to INVOICE_NO, DATE, TOTAL, VENDOR on word spans.
    Greedy longest substring match in word space.
    """
    labels = ["O"] * len(words)
    if len(words) != len(bboxes):
        raise ValueError("words and bboxes length mismatch")

    canonical = _canonical_for_weak_labels(corrected_fields)

    field_to_label = {
        "invoice_no": "INVOICE_NO",
        "date": "DATE",
        "total": "TOTAL",
        "vendor": "VENDOR",
    }

    for field_key, label_name in field_to_label.items():
        target = canonical.get(field_key)
        if not target:
            continue
        tn = _norm(str(target))
        if not tn:
            continue
        best: tuple[float, int, int] = (-1.0, -1, -1)
        n = len(words)
        for i in range(n):
            acc = ""
            for j in range(i, min(i + 40, n)):
                acc = _norm(acc + " " + words[j] if acc else words[j])
                if not acc:
                    continue
                if tn in acc or acc in tn:
                    score = len(acc) / max(len(tn), 1)
                    if score > best[0]:
                        best = (score, i, j)
                if len(acc) > len(tn) * 3:
                    break
        if best[1] >= 0:
            _, i, j = best
            for k in range(i, j + 1):
                labels[k] = label_name
    return labels
