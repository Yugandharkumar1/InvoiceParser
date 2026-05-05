"""End-to-end: OCR → ML → rules → hybrid merge."""

from __future__ import annotations

from pathlib import Path
from typing import Optional

from .config_loader import AppConfig
from .ml.hybrid_merge import HybridParseResult, build_hybrid_result
from .ml.layoutlm_engine import LayoutLMEngine, aggregate_fields_from_bio_words
from .ml.labels import FIELD_KEYS
from .ocr import (
    OcrPage,
    cache_key,
    flatten_pages_to_words,
    full_text_from_pages,
    load_cached_ocr,
    ocr_image_bytes,
    ocr_pdf_bytes,
    pages_from_cached,
    save_cached_ocr,
    sniff_is_pdf,
)
from .rules import RuleResult, VendorPluginRegistry, extract_with_rules, register_example_plugins


def rule_result_to_field_dict(
    r: RuleResult,
    fixed_confidence: dict[str, float],
) -> dict[str, tuple[Optional[str], float]]:
    out: dict[str, tuple[Optional[str], float]] = {}
    for k in FIELD_KEYS:
        v = getattr(r, k, None)
        if v:
            out[k] = (v, fixed_confidence.get(k, 0.72))
        else:
            out[k] = (None, 0.0)
    return out


def run_parse(
    file_bytes: bytes,
    cfg: AppConfig,
    engine: LayoutLMEngine,
    vendor_key: Optional[str] = None,
    filename: str = "upload",
) -> tuple[HybridParseResult, list[OcrPage], str]:
    """Returns hybrid result, OCR pages, full text."""
    lang = cfg.ocr.language
    root = Path(__file__).resolve().parents[1]
    cache_dir = root / cfg.ocr.cache_dir
    key = cache_key(file_bytes, lang)

    pages: list[OcrPage]
    if cfg.ocr.cache_enabled:
        cached = load_cached_ocr(cache_dir, key)
        if cached is not None:
            pages = pages_from_cached(cached)
        else:
            pages = _ocr_file(file_bytes, lang)
            save_cached_ocr(cache_dir, key, pages)
    else:
        pages = _ocr_file(file_bytes, lang)

    full_text = full_text_from_pages(pages)
    words, boxes = flatten_pages_to_words(pages)

    registry = VendorPluginRegistry()
    register_example_plugins(registry)
    overlay = registry.get_overlay(vendor_key)
    extra = None
    if overlay:
        extra = overlay
    rule_struct = extract_with_rules(full_text, extra_patterns=extra)
    rule_dict = rule_result_to_field_dict(rule_struct, cfg.rules.fixed_confidence)

    ml_dict: dict[str, tuple[Optional[str], float]] = {k: (None, 0.0) for k in FIELD_KEYS}
    if engine.is_trained and words:
        tok = engine.predict_words(words, boxes)
        if tok.word_labels:
            ml_dict = aggregate_fields_from_bio_words(words, tok.word_labels, tok.word_confidences)

    hybrid = build_hybrid_result(
        ml_dict,
        rule_dict,
        cfg.rules.fixed_confidence,
        cfg.hybrid.ml_confidence_threshold,
        cfg.hybrid.prefer_agreement_boost,
    )
    return hybrid, pages, full_text


def _ocr_file(file_bytes: bytes, lang: str) -> list[OcrPage]:
    if sniff_is_pdf(file_bytes):
        return ocr_pdf_bytes(file_bytes, lang=lang)
    return ocr_image_bytes(file_bytes, lang=lang)
