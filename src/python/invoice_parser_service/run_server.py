"""Run: python run_server.py (from invoice_parser_service directory)."""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import uvicorn

if __name__ == "__main__":
    from src.config_loader import load_config

    cfg = load_config(ROOT / "config.yaml")
    uvicorn.run(
        "app.main:app",
        host=cfg.service.host,
        port=cfg.service.port,
        reload=False,
        factory=False,
    )
