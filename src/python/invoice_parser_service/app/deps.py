"""Application singletons (config + ML engine)."""

from __future__ import annotations

from pathlib import Path

from src.config_loader import AppConfig, load_config
from src.ml.layoutlm_engine import LayoutLMEngine
from src.versioning.resolver import resolve_active_model_path

_config: AppConfig | None = None
_engine: LayoutLMEngine | None = None


def get_root() -> Path:
    return Path(__file__).resolve().parents[1]


def get_config() -> AppConfig:
    global _config
    if _config is None:
        _config = load_config(get_root() / "config.yaml")
    return _config


def get_engine() -> LayoutLMEngine:
    global _engine
    if _engine is None:
        cfg = get_config()
        root = get_root()
        path = resolve_active_model_path(
            root,
            cfg.ml.active_model_path,
            cfg.versioning.models_root,
        )
        dev = cfg.ml.device or ("cuda" if __import__("torch").cuda.is_available() else None)
        _engine = LayoutLMEngine(
            path,
            base_model_name=cfg.ml.base_model,
            max_length=cfg.ml.max_length,
            device=dev,
        )
    return _engine


def reload_engine() -> None:
    global _engine
    _engine = None
    get_engine()
