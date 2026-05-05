"""Compare new model metrics to active model; promote or rollback."""

from __future__ import annotations

import json
import shutil
from pathlib import Path
from typing import Any, Optional


def load_metrics(model_dir: Path) -> dict[str, Any]:
    p = model_dir / "metrics.json"
    if not p.is_file():
        return {}
    return json.loads(p.read_text(encoding="utf-8"))


def should_promote(
    old_metrics: dict[str, Any],
    new_metrics: dict[str, Any],
    min_delta: float,
    regression_tolerance: float,
) -> tuple[bool, str]:
    old_acc = float(old_metrics.get("eval_accuracy", 0.0))
    new_acc = float(new_metrics.get("eval_accuracy", 0.0))
    if new_acc < old_acc - regression_tolerance:
        return False, f"Regression: new accuracy {new_acc:.4f} < old {old_acc:.4f} - tolerance {regression_tolerance}"
    if new_acc < old_acc + min_delta and old_acc > 0:
        return False, f"Improvement {new_acc - old_acc:.4f} below min_delta {min_delta}"
    return True, f"Promoting: eval_accuracy {old_acc:.4f} -> {new_acc:.4f}"


def write_active_pointer(models_root: Path, version_rel: str) -> None:
    models_root.mkdir(parents=True, exist_ok=True)
    active = models_root / "active.json"
    active.write_text(
        json.dumps({"path": version_rel, "version": version_rel}, indent=2),
        encoding="utf-8",
    )


def copy_tree_safe(src: Path, dst: Path) -> None:
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)


def promote_or_keep(
    models_root: Path,
    new_version_dir: Path,
    min_delta: float,
    regression_tolerance: float,
) -> tuple[bool, str]:
    """
    If models_root/versions/<prev> exists with metrics, compare.
    If no previous model, always promote new.
    """
    versions = models_root / "versions"
    prev_dirs = sorted([d for d in versions.iterdir() if d.is_dir()], key=lambda x: x.name, reverse=True)
    # exclude the new dir we just added (same parent) — caller passes new_version_dir inside versions/
    new_metrics = load_metrics(new_version_dir)
    if not prev_dirs:
        write_active_pointer(models_root, str(new_version_dir.relative_to(models_root)))
        return True, "First model version promoted."

    # previous best = active.json or second-newest
    active_file = models_root / "active.json"
    old_dir: Optional[Path] = None
    if active_file.is_file():
        try:
            data = json.loads(active_file.read_text(encoding="utf-8"))
            rel = data.get("path")
            if rel:
                cand = models_root / rel
                if cand.is_dir() and cand.resolve() != new_version_dir.resolve():
                    old_dir = cand
        except (json.JSONDecodeError, OSError):
            pass
    if old_dir is None:
        for d in prev_dirs:
            if d.resolve() != new_version_dir.resolve():
                old_dir = d
                break

    if old_dir is None or not old_dir.is_dir():
        write_active_pointer(models_root, str(new_version_dir.relative_to(models_root)))
        return True, "No previous model to compare; promoted."

    old_metrics = load_metrics(old_dir)
    ok, msg = should_promote(old_metrics, new_metrics, min_delta, regression_tolerance)
    if ok:
        write_active_pointer(models_root, str(new_version_dir.relative_to(models_root)))
        return True, msg
    return False, msg
