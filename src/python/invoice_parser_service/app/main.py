"""FastAPI: hybrid invoice parsing + feedback + retrain."""

from __future__ import annotations

import asyncio
import logging
import uuid
from pathlib import Path

from fastapi import Depends, File, Form, UploadFile
from fastapi import FastAPI
from fastapi.responses import JSONResponse

from app.deps import get_config, get_engine, get_root, reload_engine
from app.schemas import FeedbackRequest, FeedbackResponse, ParseResponse, RetrainResponse
from src.feedback.store import append_feedback, load_all_feedback
from src.feedback.weak_labels import assign_word_labels_from_corrections
from src.parse_pipeline import run_parse
from src.ocr.tesseract_layer import serialize_ocr_page_for_api
from src.training.promote import promote_or_keep
from src.training.train_layoutlm import train_from_feedback_file

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(title="Invoice Parser ML Service", version="1.0.0")

_retrain_lock = asyncio.Lock()
_last_feedback_count_for_train = 0


@app.on_event("startup")
async def startup() -> None:
    get_config()
    eng = get_engine()
    logger.info("LayoutLM trained checkpoint loaded: %s", eng.is_trained)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok", "ml_ready": str(get_engine().is_trained)}


@app.post("/parse", response_model=ParseResponse)
async def parse_invoice(
    file: UploadFile = File(...),
    vendor_key: str | None = Form(default=None),
) -> ParseResponse:
    data = await file.read()
    cfg = get_config()
    engine = get_engine()
    hybrid, pages, _full = run_parse(data, cfg, engine, vendor_key=vendor_key, filename=file.filename or "upload")

    ocr_pages = [serialize_ocr_page_for_api(p) for p in pages]

    pid = str(uuid.uuid4())
    return ParseResponse(
        invoice_no=hybrid.invoice_no,
        date=hybrid.date,
        total=hybrid.total,
        vendor=hybrid.vendor,
        confidence=hybrid.confidence,
        source=hybrid.source,
        per_field_source={k: v for k, v in hybrid.per_field_source.items()},
        alternatives=hybrid.alternatives,
        ocr={"pages": ocr_pages},
        parse_id=pid,
    )


@app.post("/feedback", response_model=FeedbackResponse)
async def feedback(req: FeedbackRequest) -> FeedbackResponse:
    cfg = get_config()
    root = get_root()
    lock_path = root / cfg.feedback.lock_path
    dataset_path = root / cfg.feedback.dataset_path

    pages_out: list[dict] = []
    if req.pages:
        for pg in req.pages:
            words = pg.words
            bboxes = pg.bboxes
            wlabels = pg.labels
            if not wlabels and req.corrected_fields:
                wlabels = assign_word_labels_from_corrections(words, bboxes, req.corrected_fields)
            labels_final = wlabels if wlabels and len(wlabels) == len(words) else ["O"] * len(words)
            pages_out.append({"words": words, "bboxes": bboxes, "labels": labels_final})
    record = {
        "invoice_id": req.invoice_id,
        "pages": pages_out,
        "corrected_fields": req.corrected_fields,
    }
    fid = append_feedback(lock_path, dataset_path, record)

    global _last_feedback_count_for_train
    rows = load_all_feedback(dataset_path)
    training_scheduled = False
    if len(rows) - _last_feedback_count_for_train >= cfg.training.min_new_feedback_samples:
        training_scheduled = True
        asyncio.create_task(_maybe_retrain())

    return FeedbackResponse(feedback_id=fid, training_scheduled=training_scheduled)


async def _maybe_retrain() -> None:
    async with _retrain_lock:
        cfg = get_config()
        root = get_root()
        dataset_path = root / cfg.feedback.dataset_path
        rows = load_all_feedback(dataset_path)
        if len(rows) < cfg.training.min_new_feedback_samples:
            return
        version_name = __import__("datetime").datetime.utcnow().strftime("v%Y%m%d%H%M%S")
        out_dir = root / cfg.versioning.models_root / "versions" / version_name
        try:
            metrics = await asyncio.to_thread(
                train_from_feedback_file,
                dataset_path,
                out_dir,
                cfg.ml.base_model,
                cfg.ml.max_length,
                cfg.training.epochs,
                cfg.training.batch_size,
                cfg.training.learning_rate,
            )
            ok, msg = promote_or_keep(
                root / cfg.versioning.models_root,
                out_dir,
                cfg.versioning.min_accuracy_delta_to_promote,
                cfg.versioning.regression_tolerance,
            )
            logger.info("Retrain: %s — promoted=%s metrics=%s", msg, ok, metrics)
            if ok:
                reload_engine()
            global _last_feedback_count_for_train
            _last_feedback_count_for_train = len(rows)
        except Exception as e:
            logger.exception("Retrain failed: %s", e)


@app.post("/admin/retrain", response_model=RetrainResponse)
async def admin_retrain() -> RetrainResponse:
    """Manual full retrain + version + promote check."""
    cfg = get_config()
    root = get_root()
    dataset_path = root / cfg.feedback.dataset_path
    version_name = __import__("datetime").datetime.utcnow().strftime("v%Y%m%d%H%M%S")
    out_dir = root / cfg.versioning.models_root / "versions" / version_name
    try:
        metrics = train_from_feedback_file(
            dataset_path,
            out_dir,
            cfg.ml.base_model,
            cfg.ml.max_length,
            cfg.training.epochs,
            cfg.training.batch_size,
            cfg.training.learning_rate,
        )
        ok, msg = promote_or_keep(
            root / cfg.versioning.models_root,
            out_dir,
            cfg.versioning.min_accuracy_delta_to_promote,
            cfg.versioning.regression_tolerance,
        )
        if ok:
            reload_engine()
        return RetrainResponse(success=ok, message=msg, metrics=metrics)
    except Exception as e:
        return RetrainResponse(success=False, message=str(e), metrics=None)
