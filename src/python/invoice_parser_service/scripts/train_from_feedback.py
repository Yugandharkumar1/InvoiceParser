"""CLI: train LayoutLM from TrainingData/feedback_dataset.json into models/versions/<ts>."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from src.config_loader import load_config
from src.training.promote import promote_or_keep
from src.training.train_layoutlm import train_from_feedback_file


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", type=Path, default=ROOT / "config.yaml")
    args = ap.parse_args()
    cfg = load_config(args.config)
    root = ROOT
    dataset = root / cfg.feedback.dataset_path
    from datetime import datetime

    version = datetime.utcnow().strftime("v%Y%m%d%H%M%S")
    out = root / cfg.versioning.models_root / "versions" / version
    metrics = train_from_feedback_file(
        dataset,
        out,
        cfg.ml.base_model,
        cfg.ml.max_length,
        cfg.training.epochs,
        cfg.training.batch_size,
        cfg.training.learning_rate,
    )
    ok, msg = promote_or_keep(
        root / cfg.versioning.models_root,
        out,
        cfg.versioning.min_accuracy_delta_to_promote,
        cfg.versioning.regression_tolerance,
    )
    print(msg)
    print("metrics:", metrics)
    print("promoted:", ok)


if __name__ == "__main__":
    main()
