"""Token classification labels for LayoutLM."""

from __future__ import annotations

# Order must match classifier head id2label
LABEL_LIST = ["O", "INVOICE_NO", "DATE", "TOTAL", "VENDOR"]
LABEL_TO_ID = {l: i for i, l in enumerate(LABEL_LIST)}
ID_TO_LABEL = {i: l for i, l in enumerate(LABEL_LIST)}

FIELD_KEYS = ("invoice_no", "date", "total", "vendor")
LABEL_TO_FIELD = {
    "INVOICE_NO": "invoice_no",
    "DATE": "date",
    "TOTAL": "total",
    "VENDOR": "vendor",
}
