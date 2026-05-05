"""Resolve which fine-tuned model directory to load."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Optional


def resolve_active_model_path(root: Path, config_path: Optional[str], models_root: str) -> Optional[Path]:
    if config_path:
        p = Path(config_path)
        if p.is_dir():
            return p
    base = root / models_root
    active_file = base / "active.json"
    if active_file.is_file():
        try:
            data = json.loads(active_file.read_text(encoding="utf-8"))
            rel = data.get("path") or data.get("version_path")
            if rel:
                cand = base / rel if not Path(rel).is_absolute() else Path(rel)
                if cand.is_dir():
                    return cand
        except (json.JSONDecodeError, OSError):
            pass
    # newest version directory by name
    versions = base / "versions"
    if versions.is_dir():
        dirs = sorted([d for d in versions.iterdir() if d.is_dir()], key=lambda x: x.name, reverse=True)
        if dirs:
            return dirs[0]
    return None
