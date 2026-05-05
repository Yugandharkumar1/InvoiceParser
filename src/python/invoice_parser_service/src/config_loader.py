"""Load YAML config with optional env overrides."""

from __future__ import annotations

import os
from pathlib import Path
from typing import Any, Optional

import yaml
from pydantic import BaseModel, Field


class ServiceConfig(BaseModel):
    host: str = "0.0.0.0"
    port: int = 8765


class OcrConfig(BaseModel):
    tesseract_cmd: Optional[str] = None
    language: str = "eng"
    cache_dir: str = ".cache/ocr"
    cache_enabled: bool = True


class RulesConfig(BaseModel):
    fixed_confidence: dict[str, float] = Field(
        default_factory=lambda: {
            "invoice_no": 0.72,
            "date": 0.70,
            "total": 0.74,
            "vendor": 0.65,
        }
    )


class HybridConfig(BaseModel):
    ml_confidence_threshold: float = 0.55
    prefer_agreement_boost: float = 0.05


class MlConfig(BaseModel):
    base_model: str = "microsoft/layoutlm-base-uncased"
    max_length: int = 512
    device: Optional[str] = None
    active_model_path: Optional[str] = None


class TrainingConfig(BaseModel):
    min_new_feedback_samples: int = 10
    batch_size: int = 4
    epochs: int = 3
    learning_rate: float = 5e-5
    use_gpu: bool = True


class VersioningConfig(BaseModel):
    models_root: str = "models"
    min_accuracy_delta_to_promote: float = 0.005
    regression_tolerance: float = 0.02


class FeedbackConfig(BaseModel):
    dataset_path: str = "TrainingData/feedback_dataset.json"
    lock_path: str = "TrainingData/feedback_dataset.lock"


class AppConfig(BaseModel):
    service: ServiceConfig = Field(default_factory=ServiceConfig)
    ocr: OcrConfig = Field(default_factory=OcrConfig)
    rules: RulesConfig = Field(default_factory=RulesConfig)
    hybrid: HybridConfig = Field(default_factory=HybridConfig)
    ml: MlConfig = Field(default_factory=MlConfig)
    training: TrainingConfig = Field(default_factory=TrainingConfig)
    versioning: VersioningConfig = Field(default_factory=VersioningConfig)
    feedback: FeedbackConfig = Field(default_factory=FeedbackConfig)


def load_config(config_path: Optional[Path] = None) -> AppConfig:
    root = Path(__file__).resolve().parents[1]
    path = config_path or root / "config.yaml"
    data: dict[str, Any] = {}
    if path.is_file():
        with open(path, encoding="utf-8") as f:
            data = yaml.safe_load(f) or {}

    if os.environ.get("INVOICE_PARSER_SERVICE_PORT"):
        data.setdefault("service", {})["port"] = int(os.environ["INVOICE_PARSER_SERVICE_PORT"])
    if os.environ.get("INVOICE_PARSER_SERVICE_HOST"):
        data.setdefault("service", {})["host"] = os.environ["INVOICE_PARSER_SERVICE_HOST"]
    if os.environ.get("INVOICE_PARSER_TESSERACT_CMD"):
        data.setdefault("ocr", {})["tesseract_cmd"] = os.environ["INVOICE_PARSER_TESSERACT_CMD"]
    if os.environ.get("INVOICE_PARSER_MODELS_ROOT"):
        data.setdefault("versioning", {})["models_root"] = os.environ["INVOICE_PARSER_MODELS_ROOT"]
    if os.environ.get("INVOICE_PARSER_ACTIVE_MODEL"):
        data.setdefault("ml", {})["active_model_path"] = os.environ["INVOICE_PARSER_ACTIVE_MODEL"]
    if os.environ.get("INVOICE_PARSER_MIN_FEEDBACK_FOR_TRAIN"):
        data.setdefault("training", {})["min_new_feedback_samples"] = int(
            os.environ["INVOICE_PARSER_MIN_FEEDBACK_FOR_TRAIN"]
        )

    return AppConfig.model_validate(data)
