"""Append-only feedback dataset with file lock."""

from __future__ import annotations

import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

from filelock import FileLock


def append_feedback(
    lock_path: Path,
    dataset_path: Path,
    record: dict[str, Any],
) -> str:
    dataset_path.parent.mkdir(parents=True, exist_ok=True)
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    rid = str(record.get("id") or uuid.uuid4())
    record["id"] = rid
    record.setdefault("created_at", datetime.now(timezone.utc).isoformat())

    with FileLock(str(lock_path), timeout=60):
        rows: list[Any] = []
        if dataset_path.is_file():
            try:
                rows = json.loads(dataset_path.read_text(encoding="utf-8"))
                if not isinstance(rows, list):
                    rows = []
            except (json.JSONDecodeError, OSError):
                rows = []
        rows.append(record)
        dataset_path.write_text(json.dumps(rows, indent=2, ensure_ascii=False), encoding="utf-8")
    return rid


def load_all_feedback(dataset_path: Path) -> list[dict[str, Any]]:
    if not dataset_path.is_file():
        return []
    try:
        data = json.loads(dataset_path.read_text(encoding="utf-8"))
        return data if isinstance(data, list) else []
    except (json.JSONDecodeError, OSError):
        return []


def count_feedback_since(dataset_path: Path, since_iso: Optional[str] = None) -> int:
    rows = load_all_feedback(dataset_path)
    if not since_iso:
        return len(rows)
    # optional filter by created_at > since_iso
    n = 0
    for r in rows:
        ts = r.get("created_at") or ""
        if ts > since_iso:
            n += 1
    return n
