"""Modular rule-based extraction: regex + keywords. Always produces a baseline."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Optional


@dataclass
class RuleResult:
    invoice_no: Optional[str] = None
    date: Optional[str] = None
    total: Optional[str] = None
    vendor: Optional[str] = None
    # which pattern name matched (debug)
    matched_rules: dict[str, str] = field(default_factory=dict)


# Default regex / keyword patterns (extensible via VendorPluginRegistry)
INVOICE_NO_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("invoice_hash", re.compile(r"Invoice\s*#\s*:?\s*([A-Za-z0-9\-/]+)", re.I)),
    ("invoice_number_colon", re.compile(r"Invoice\s*Number\s*:?\s*([A-Za-z0-9\-/]+)", re.I)),
    ("invoice_no_label", re.compile(r"Invoice\s+No\.?\s*:?\s*([A-Za-z0-9\-/]+)", re.I)),
    ("bill_number", re.compile(r"Bill\s*Number\s*:?\s*([A-Za-z0-9\-/]+)", re.I)),
]

DATE_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("mdy_slash", re.compile(r"\b(\d{1,2}/\d{1,2}/\d{2,4})\b")),
    ("iso_like", re.compile(r"\b(\d{4}-\d{1,2}-\d{1,2})\b")),
    ("month_d_y", re.compile(r"\b((?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})\b", re.I)),
]

TOTAL_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("total_due", re.compile(r"(?:Total\s+Amount\s+Due|Amount\s+Due|Total\s+Due|Balance\s+Due)\s*:?\s*\$?\s*([\d,]+\.?\d*)", re.I)),
    ("total_keyword", re.compile(r"(?:^|\b)Total\b\s*:?\s*\$?\s*([\d,]+\.?\d*)", re.I)),
    ("grand_total", re.compile(r"Grand\s+Total\s*:?\s*\$?\s*([\d,]+\.?\d*)", re.I)),
]

# First lines often contain vendor (letterhead heuristic)
_VENDOR_SKIP = re.compile(
    r"^(invoice|bill|statement|page|tel:|fax|www\.|http)", re.I
)


def _first_meaningful_line(text: str) -> Optional[str]:
    for line in text.splitlines():
        line = line.strip()
        if len(line) < 3:
            continue
        if _VENDOR_SKIP.search(line):
            continue
        if re.match(r"^\d+[./-]", line):
            continue
        return line[:200]
    return None


def extract_with_rules(
    full_text: str,
    extra_patterns: Optional[dict[str, list[tuple[str, re.Pattern[str]]]]] = None,
) -> RuleResult:
    """Extract invoice_no, date, total, vendor using baseline + optional vendor overlays."""
    result = RuleResult()
    extra = extra_patterns or {}

    def apply_patterns(
        field: str,
        patterns: list[tuple[str, re.Pattern[str]]],
    ) -> None:
        for name, pat in patterns:
            m = pat.search(full_text)
            if m and m.lastindex:
                val = m.group(1).strip()
                if val:
                    setattr(result, field, val)
                    result.matched_rules[field] = name
                    return

    inv_patterns = list(INVOICE_NO_PATTERNS)
    inv_patterns.extend(extra.get("invoice_no", []))
    apply_patterns("invoice_no", inv_patterns)

    date_patterns = list(DATE_PATTERNS)
    date_patterns.extend(extra.get("date", []))
    apply_patterns("date", date_patterns)

    total_patterns = list(TOTAL_PATTERNS)
    total_patterns.extend(extra.get("total", []))
    apply_patterns("total", total_patterns)

    vendor_patterns = list(extra.get("vendor", []))
    for name, pat in vendor_patterns:
        m = pat.search(full_text)
        if m and m.lastindex:
            result.vendor = m.group(1).strip()
            result.matched_rules["vendor"] = name
            break
    if not result.vendor:
        result.vendor = _first_meaningful_line(full_text)
        if result.vendor:
            result.matched_rules["vendor"] = "first_line_heuristic"

    return result


class VendorPluginRegistry:
    """Register vendor-specific pattern lists without removing base rules."""

    def __init__(self) -> None:
        self._plugins: dict[str, dict[str, list[tuple[str, re.Pattern[str]]]]] = {}

    def register(
        self,
        vendor_key: str,
        patterns: dict[str, list[tuple[str, re.Pattern[str]]]],
    ) -> None:
        self._plugins[vendor_key] = patterns

    def get_overlay(self, vendor_key: Optional[str]) -> Optional[dict[str, list[tuple[str, re.Pattern[str]]]]]:
        if not vendor_key:
            return None
        return self._plugins.get(vendor_key.lower())
