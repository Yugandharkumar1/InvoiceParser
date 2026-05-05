"""Example vendor-specific rule overlays. Register patterns without removing base rules."""

from __future__ import annotations

import re

from .base_rules import VendorPluginRegistry


def register_example_plugins(registry: VendorPluginRegistry) -> None:
    """Register optional vendor keys (e.g. from carrier code in C#)."""
    registry.register(
        "example_corp",
        {
            "invoice_no": [
                ("ec_inv", re.compile(r"EC-INV-\s*(\d+)", re.I)),
            ],
            "total": [
                ("ec_pay", re.compile(r"Pay\s+This\s+Amount\s*\$?\s*([\d,]+\.?\d*)", re.I)),
            ],
        },
    )
