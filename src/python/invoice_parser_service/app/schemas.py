from __future__ import annotations

from typing import Any, Optional

from pydantic import BaseModel, ConfigDict, Field


class OcrPageOut(BaseModel):
    words: list[str]
    bboxes: list[list[float]]


class OcrPageIn(BaseModel):
    model_config = ConfigDict(extra="ignore")

    words: list[str] = Field(default_factory=list)
    bboxes: list[list[float]] = Field(default_factory=list)
    labels: list[str] | None = None


class ParseResponse(BaseModel):
    invoice_no: str = ""
    date: str = ""
    total: str = ""
    vendor: str = ""
    confidence: dict[str, float] = Field(default_factory=dict)
    source: str = "RULE"
    per_field_source: dict[str, str] = Field(default_factory=dict)
    alternatives: dict[str, dict[str, Optional[str]]] = Field(default_factory=dict)
    ocr: dict[str, Any] = Field(default_factory=dict)
    parse_id: str = ""


class FeedbackRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    invoice_id: str = ""
    corrected_fields: dict[str, Optional[str]] = Field(default_factory=dict)
    """If ocr pages omitted, feedback still stored but cannot train LayoutLM until pages provided."""
    pages: Optional[list[OcrPageIn]] = None


class FeedbackResponse(BaseModel):
    status: str = "ok"
    feedback_id: str
    training_scheduled: bool = False


class RetrainResponse(BaseModel):
    success: bool
    message: str
    metrics: Optional[dict[str, Any]] = None
