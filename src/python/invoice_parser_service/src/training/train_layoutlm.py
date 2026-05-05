"""Train LayoutLM token classifier from feedback_dataset.json."""

from __future__ import annotations

import json
import random
from pathlib import Path
from typing import Any

import numpy as np
import torch
from torch.utils.data import Dataset
from transformers import (
    LayoutLMForTokenClassification,
    LayoutLMTokenizerFast,
    Trainer,
    TrainingArguments,
)

from ..ml.labels import ID_TO_LABEL, LABEL_LIST


class InvoiceTokenDataset(Dataset):
    def __init__(
        self,
        encodings: dict[str, list[Any]],
        labels: list[list[int]],
    ) -> None:
        self.encodings = encodings
        self.labels = labels

    def __getitem__(self, idx: int) -> dict[str, Any]:
        item = {k: torch.tensor(v[idx]) for k, v in self.encodings.items()}
        item["labels"] = torch.tensor(self.labels[idx])
        return item

    def __len__(self) -> int:
        return len(self.labels)


def _align_labels_with_tokens(
    encoding: Any,
    word_labels: list[str],
    label_to_id: dict[str, int],
) -> list[int]:
    word_ids = encoding.word_ids(batch_index=0)
    seq_len = encoding["input_ids"].shape[1]
    label_ids = [-100] * seq_len
    for i, wid in enumerate(word_ids):
        if wid is None:
            continue
        if wid < len(word_labels):
            lab = word_labels[wid]
            label_ids[i] = label_to_id.get(lab, label_to_id["O"])
    return label_ids


def build_samples_from_feedback(
    rows: list[dict[str, Any]],
    tokenizer: LayoutLMTokenizerFast,
    max_length: int,
) -> tuple[dict[str, list[Any]], list[list[int]]]:
    input_ids: list[list[int]] = []
    attention_mask: list[list[int]] = []
    bbox: list[list[list[int]]] = []
    all_label_ids: list[list[int]] = []

    label_to_id = {l: i for i, l in enumerate(LABEL_LIST)}

    for row in rows:
        for page in row.get("pages") or []:
            words = page.get("words") or []
            bboxes = page.get("bboxes") or []
            wlabels = page.get("labels") or []
            if not words or len(words) != len(bboxes) or len(wlabels) != len(words):
                continue

            enc = tokenizer(
                words,
                boxes=bboxes,
                padding="max_length",
                truncation=True,
                max_length=max_length,
                return_tensors="pt",
            )
            label_ids = _align_labels_with_tokens(enc, wlabels, label_to_id)
            input_ids.append(enc["input_ids"][0].tolist())
            attention_mask.append(enc["attention_mask"][0].tolist())
            bbox.append(enc["bbox"][0].tolist())
            all_label_ids.append(label_ids)

    return (
        {
            "input_ids": input_ids,
            "attention_mask": attention_mask,
            "bbox": bbox,
        },
        all_label_ids,
    )


def train_from_feedback_file(
    feedback_path: Path,
    output_dir: Path,
    base_model: str = "microsoft/layoutlm-base-uncased",
    max_length: int = 512,
    epochs: int = 3,
    batch_size: int = 4,
    lr: float = 5e-5,
    seed: int = 42,
    eval_split: float = 0.15,
) -> dict[str, Any]:
    rows = json.loads(feedback_path.read_text(encoding="utf-8"))
    if not isinstance(rows, list) or len(rows) < 1:
        raise ValueError("feedback_dataset.json must be a non-empty list")

    tokenizer = LayoutLMTokenizerFast.from_pretrained(base_model)
    encodings, label_ids = build_samples_from_feedback(rows, tokenizer, max_length)
    if len(label_ids) < 5:
        raise ValueError("Need at least 5 labeled pages in feedback to train.")

    rng = random.Random(seed)
    idx = list(range(len(label_ids)))
    rng.shuffle(idx)
    n_eval = max(1, int(len(idx) * eval_split))
    eval_set = set(idx[:n_eval])
    train_idx = [i for i in idx if i not in eval_set]
    eval_idx = [i for i in idx if i in eval_set]

    def subset(enc: dict[str, list], indices: list[int]) -> dict[str, list]:
        return {k: [enc[k][i] for i in indices] for k in enc}

    train_enc = subset(encodings, train_idx)
    train_labels = [label_ids[i] for i in train_idx]
    eval_enc = subset(encodings, eval_idx)
    eval_labels = [label_ids[i] for i in eval_idx]

    train_ds = InvoiceTokenDataset(train_enc, train_labels)
    eval_ds = InvoiceTokenDataset(eval_enc, eval_labels)

    model = LayoutLMForTokenClassification.from_pretrained(
        base_model,
        num_labels=len(LABEL_LIST),
        id2label={i: l for i, l in enumerate(LABEL_LIST)},
        label2id={l: i for i, l in enumerate(LABEL_LIST)},
    )

    args = TrainingArguments(
        output_dir=str(output_dir / "trainer_output"),
        num_train_epochs=epochs,
        per_device_train_batch_size=batch_size,
        per_device_eval_batch_size=batch_size,
        learning_rate=lr,
        evaluation_strategy="epoch",
        save_strategy="epoch",
        load_best_model_at_end=True,
        metric_for_best_model="eval_loss",
        seed=seed,
        logging_steps=10,
    )

    def compute_metrics(eval_pred: Any) -> dict[str, float]:
        logits, labels = eval_pred
        preds = np.argmax(logits, axis=-1)
        true_preds: list[int] = []
        true_labels: list[int] = []
        for p, l in zip(preds.flatten(), labels.flatten()):
            if int(l) == -100:
                continue
            true_preds.append(int(p))
            true_labels.append(int(l))
        if not true_preds:
            return {"accuracy": 0.0}
        acc = float(np.mean(np.array(true_preds) == np.array(true_labels)))
        return {"accuracy": acc}

    trainer = Trainer(
        model=model,
        args=args,
        train_dataset=train_ds,
        eval_dataset=eval_ds,
        compute_metrics=compute_metrics,
    )
    trainer.train()
    metrics = trainer.evaluate()
    output_dir.mkdir(parents=True, exist_ok=True)
    trainer.save_model(str(output_dir))
    tokenizer.save_pretrained(str(output_dir))

    summary = {
        "eval_accuracy": float(metrics.get("eval_accuracy", 0.0)),
        "eval_loss": float(metrics.get("eval_loss", 0.0)),
        "train_samples": len(train_labels),
        "eval_samples": len(eval_labels),
        "label_map": ID_TO_LABEL,
    }
    (output_dir / "metrics.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary
