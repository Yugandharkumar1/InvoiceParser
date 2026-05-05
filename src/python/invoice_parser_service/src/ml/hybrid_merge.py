"""Merge ML + rule outputs with confidence; never discard rule baseline from response."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal, Optional

SourceKind = Literal["ML", "RULE", "HYBRID"]


@dataclass
class FieldDecision:
    value: Optional[str]
    confidence: float
    source: SourceKind
    ml_value: Optional[str] = None
    ml_confidence: float = 0.0
    rule_value: Optional[str] = None
    rule_confidence: float = 0.0


@dataclass
class HybridParseResult:
    invoice_no: str
    date: str
    total: str
    vendor: str
    confidence: dict[str, float]
    source: str  # overall tag: ML | RULE | HYBRID
    per_field_source: dict[str, SourceKind]
    alternatives: dict[str, dict[str, Optional[str]]]


def _norm(s: Optional[str]) -> str:
    if s is None:
        return ""
    return " ".join(s.split()).strip().lower()


def _agree(a: Optional[str], b: Optional[str]) -> bool:
    if not a or not b:
        return False
    return _norm(a) == _norm(b) or _norm(a) in _norm(b) or _norm(b) in _norm(a)


def merge_field(
    field_key: str,
    ml_val: Optional[str],
    ml_conf: float,
    rule_val: Optional[str],
    rule_conf: float,
    ml_threshold: float,
    agreement_boost: float,
) -> FieldDecision:
    """Choose highest effective confidence; boost when ML and rules agree."""
    ml_conf_eff = ml_conf
    rule_conf_eff = rule_conf

    if _agree(ml_val, rule_val) and ml_val and rule_val:
        ml_conf_eff = min(1.0, ml_conf + agreement_boost)
        rule_conf_eff = min(1.0, rule_conf + agreement_boost)

    use_ml = (
        ml_val
        and ml_conf_eff >= ml_threshold
        and (ml_conf_eff >= rule_conf_eff or not rule_val)
    )
    use_rule = rule_val and (
        not use_ml or rule_conf_eff >= ml_conf_eff or not ml_val
    )

    if use_ml and use_rule and _agree(ml_val, rule_val):
        return FieldDecision(
            value=ml_val or rule_val,
            confidence=max(ml_conf_eff, rule_conf_eff),
            source="HYBRID",
            ml_value=ml_val,
            ml_confidence=ml_conf,
            rule_value=rule_val,
            rule_confidence=rule_conf,
        )

    if use_ml and ml_val:
        return FieldDecision(
            value=ml_val,
            confidence=ml_conf_eff,
            source="ML",
            ml_value=ml_val,
            ml_confidence=ml_conf,
            rule_value=rule_val,
            rule_confidence=rule_conf,
        )

    if rule_val:
        return FieldDecision(
            value=rule_val,
            confidence=rule_conf_eff,
            source="RULE",
            ml_value=ml_val,
            ml_confidence=ml_conf,
            rule_value=rule_val,
            rule_confidence=rule_conf,
        )

    # fallback: prefer any non-empty
    if ml_val:
        return FieldDecision(
            value=ml_val,
            confidence=ml_conf_eff * 0.5,
            source="ML",
            ml_value=ml_val,
            ml_confidence=ml_conf,
            rule_value=rule_val,
            rule_confidence=rule_conf,
        )
    if rule_val:
        return FieldDecision(
            value=rule_val,
            confidence=rule_conf_eff * 0.5,
            source="RULE",
            ml_value=ml_val,
            ml_confidence=ml_conf,
            rule_value=rule_val,
            rule_confidence=rule_conf,
        )

    return FieldDecision(
        value="",
        confidence=0.0,
        source="RULE",
        ml_value=ml_val,
        ml_confidence=ml_conf,
        rule_value=rule_val,
        rule_confidence=rule_conf,
    )


def build_hybrid_result(
    ml_fields: dict[str, tuple[Optional[str], float]],
    rule_fields: dict[str, tuple[Optional[str], float]],
    fixed_rule_scores: dict[str, float],
    ml_threshold: float,
    agreement_boost: float,
) -> HybridParseResult:
    """
    ml_fields / rule_fields: field_key -> (value, confidence)
    Rule confidence uses fixed heuristic from config if tuple second value is 0.
    """
    keys = ("invoice_no", "date", "total", "vendor")
    decisions: dict[str, FieldDecision] = {}
    conf_out: dict[str, float] = {}
    per_src: dict[str, SourceKind] = {}

    for k in keys:
        mv, mc = ml_fields.get(k, (None, 0.0))
        rv, rc = rule_fields.get(k, (None, 0.0))
        if rv and rc <= 0:
            rc = fixed_rule_scores.get(k, 0.7)
        d = merge_field(k, mv, mc, rv, rc, ml_threshold, agreement_boost)
        decisions[k] = d
        conf_out[k] = d.confidence
        per_src[k] = d.source

    # overall source tag
    sources = set(per_src.values())
    if sources == {"HYBRID"} or (sources == {"ML", "RULE"} and len(sources) == 2):
        overall = "HYBRID"
    elif "HYBRID" in sources:
        overall = "HYBRID"
    elif sources == {"ML"}:
        overall = "ML"
    elif sources == {"RULE"}:
        overall = "RULE"
    else:
        overall = "HYBRID"

    return HybridParseResult(
        invoice_no=decisions["invoice_no"].value or "",
        date=decisions["date"].value or "",
        total=decisions["total"].value or "",
        vendor=decisions["vendor"].value or "",
        confidence=conf_out,
        source=overall,
        per_field_source=per_src,
        alternatives={
            "ml": {k: ml_fields.get(k, (None, 0.0))[0] for k in keys},
            "rule": {k: rule_fields.get(k, (None, 0.0))[0] for k in keys},
        },
    )
