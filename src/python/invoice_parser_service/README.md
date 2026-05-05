# Invoice Parser ML Service (Python)

Hybrid **LayoutLM** token classification + **rule-based** baseline, **Tesseract OCR** with bounding boxes (0–1000), **FastAPI** API, **feedback-driven** retraining with **versioned models** and **safe promotion**.

## Layout

```
invoice_parser_service/
  app/                 # FastAPI (main.py, schemas, deps)
  src/
    ocr/               # Tesseract + PDF render (PyMuPDF)
    rules/             # Regex + vendor plugins
    ml/                # LayoutLM engine + hybrid merge
    feedback/        # JSON store + weak labeling
    training/          # Trainer + promote/rollback
    versioning/        # Resolve active model path
  TrainingData/
    feedback_dataset.json
  models/
    versions/          # vYYYYMMDDhhmmss per train run
    active.json        # pointer to promoted version
  config.yaml
  run_server.py
```

## Setup

1. Install [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) and ensure `tesseract` is on `PATH` (or set `INVOICE_PARSER_TESSERACT_CMD`).
2. Python 3.10+ recommended.

```bash
cd invoice_parser_service
python -m venv .venv
.venv\Scripts\activate   # Windows
pip install -r requirements.txt
```

## Run API

```bash
python run_server.py
```

- `GET /health` — liveness + `ml_ready`
- `POST /parse` — multipart: `file` (PDF/image), optional `vendor_key`
- `POST /feedback` — JSON: `invoice_id`, `corrected_fields`, optional `pages` (from `/parse` response `ocr.pages`); weak labels generated if `labels` omitted
- `POST /admin/retrain` — force train + promote check

Environment overrides: `INVOICE_PARSER_SERVICE_PORT`, `INVOICE_PARSER_TESSERACT_CMD`, `INVOICE_PARSER_ACTIVE_MODEL`, `INVOICE_PARSER_MODELS_ROOT`.

## ML training

- Requires **≥5** labeled pages in `TrainingData/feedback_dataset.json` (see `TrainingData/sample_feedback_format.json`).
- First successful train writes `models/versions/<version>/` + `metrics.json`.
- **Promotion:** new model is activated only if accuracy improves enough vs `models/active.json` (see `config.yaml` versioning section). Previous weights remain on disk for rollback (point `active.json` back).

## GPU

Install CUDA-enabled PyTorch per [pytorch.org](https://pytorch.org). Training uses GPU when available.

## C# integration

The ASP.NET app calls this service via `InvoiceParser.Web/Services/PythonInvoiceIntegrationService.cs` and `InvoiceParserPython` in `appsettings.json`.
