"""LayoutLM token classification: inference + optional load."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import numpy as np
import torch
from transformers import LayoutLMForTokenClassification, LayoutLMTokenizerFast

from .labels import ID_TO_LABEL, LABEL_LIST


def _resolve_device(preference: Optional[str]) -> torch.device:
    if preference == "cpu":
        return torch.device("cpu")
    if preference == "cuda" or preference is None:
        if torch.cuda.is_available():
            return torch.device("cuda")
        return torch.device("cpu")
    return torch.device(preference)


def _model_files_exist(path: Path) -> bool:
    if not path.is_dir():
        return False
    has_config = (path / "config.json").is_file()
    has_weights = (path / "model.safetensors").is_file() or (path / "pytorch_model.bin").is_file()
    return has_config and has_weights


@dataclass
class TokenPredictResult:
    """Per word (OCR token) best label and confidence."""

    word_labels: list[str]
    word_confidences: list[float]  # max softmax for predicted class per word
    logits: Optional[np.ndarray] = None  # optional [seq, num_labels]


class LayoutLMEngine:
    def __init__(
        self,
        model_dir: Optional[Path],
        base_model_name: str = "microsoft/layoutlm-base-uncased",
        max_length: int = 512,
        device: Optional[str] = None,
    ) -> None:
        self.max_length = max_length
        self.device = _resolve_device(device)
        self._tokenizer: Optional[LayoutLMTokenizerFast] = None
        self._model: Optional[LayoutLMForTokenClassification] = None
        self._model_dir = model_dir

        self._untrained = True
        if model_dir and _model_files_exist(Path(model_dir)):
            self._load_from_dir(Path(model_dir))
        else:
            # No fine-tuned checkpoint: load tokenizer only for training; skip heavy base head in API
            self._tokenizer = LayoutLMTokenizerFast.from_pretrained(base_model_name)
            self._model = None
            self._untrained = True

    def _load_from_dir(self, path: Path) -> None:
        self._tokenizer = LayoutLMTokenizerFast.from_pretrained(str(path))
        self._model = LayoutLMForTokenClassification.from_pretrained(str(path))
        self._model.to(self.device)
        self._model.eval()
        self._model_dir = path
        self._untrained = False

    def reload(self, model_dir: Optional[Path]) -> None:
        if model_dir and _model_files_exist(Path(model_dir)):
            self._load_from_dir(Path(model_dir))

    @property
    def is_trained(self) -> bool:
        return self._model is not None and not getattr(self, "_untrained", True)

    @torch.inference_mode()
    def predict_words(
        self,
        words: list[str],
        boxes: list[list[float]],
    ) -> TokenPredictResult:
        """
        words/boxes: same length, LayoutLM box format 0-1000.
        """
        if not words or not self._model or not self._tokenizer:
            return TokenPredictResult(word_labels=[], word_confidences=[])

        # Truncate to max_length tokens (word-level)
        words = words[: self.max_length]
        boxes = boxes[: len(words)]
        if len(words) != len(boxes):
            raise ValueError("words and boxes length mismatch")

        encoding = self._tokenizer(
            words,
            boxes=boxes,
            padding="max_length",
            truncation=True,
            max_length=self.max_length,
            return_tensors="pt",
        )
        encoding = {k: v.to(self.device) for k, v in encoding.items()}
        outputs = self._model(**encoding)
        logits = outputs.logits  # [1, seq, num_labels]
        probs = torch.softmax(logits, dim=-1)[0].cpu().numpy()
        pred_ids = probs.argmax(axis=-1)

        # Map sequence positions back to first len(words) word tokens (skip CLS/PAD special handling)
        # LayoutLMTokenizerFast: first token is CLS, then words...
        input_ids = encoding["input_ids"][0].cpu().tolist()
        # Count non-pad tokens; alignment: tokenizer wordpiece expands words — we need word-level.
        # Simpler approach: take argmax for each input position that corresponds to our words — actually
        # HF returns one logit per token including special tokens. We aggregate by word using tokenizer.word_ids()
        word_ids = encoding.word_ids(batch_index=0)
        if word_ids is None:
            # fallback: strip CLS/SEP
            n = len(words)
            word_labels = [ID_TO_LABEL.get(int(pred_ids[i]), "O") for i in range(1, min(n + 1, len(pred_ids)))]
            word_conf = [float(probs[i, pred_ids[i]]) for i in range(1, min(n + 1, len(pred_ids)))]
            while len(word_labels) < n:
                word_labels.append("O")
                word_conf.append(0.0)
            return TokenPredictResult(
                word_labels=word_labels[:n],
                word_confidences=word_conf[:n],
                logits=logits[0].cpu().numpy(),
            )

        # Group by word_id
        word_best_label: dict[int, tuple[str, float]] = {}
        for idx, wid in enumerate(word_ids):
            if wid is None:
                continue
            pid = int(pred_ids[idx])
            label = ID_TO_LABEL.get(pid, "O")
            conf = float(probs[idx, pid])
            if wid not in word_best_label or conf > word_best_label[wid][1]:
                word_best_label[wid] = (label, conf)

        wl: list[str] = []
        wc: list[float] = []
        for i in range(len(words)):
            if i in word_best_label:
                wl.append(word_best_label[i][0])
                wc.append(word_best_label[i][1])
            else:
                wl.append("O")
                wc.append(0.0)

        return TokenPredictResult(
            word_labels=wl,
            word_confidences=wc,
            logits=logits[0].cpu().numpy(),
        )


def aggregate_fields_from_bio_words(
    words: list[str],
    labels: list[str],
    confidences: list[float],
) -> dict[str, tuple[Optional[str], float]]:
    """Merge consecutive tokens sharing the same label into field spans."""
    from .labels import FIELD_KEYS, LABEL_TO_FIELD

    buckets: dict[str, list[tuple[str, float]]] = {k: [] for k in FIELD_KEYS}
    i = 0
    n = len(words)
    while i < n:
        lab = labels[i] if i < len(labels) else "O"
        cf = confidences[i] if i < len(confidences) else 0.0
        fk = LABEL_TO_FIELD.get(lab)
        if fk is None:
            i += 1
            continue
        j = i
        parts: list[str] = []
        confs: list[float] = []
        while j < n and labels[j] == lab:
            parts.append(words[j])
            confs.append(confidences[j] if j < len(confidences) else 0.0)
            j += 1
        text = " ".join(parts).strip()
        mean_c = sum(confs) / len(confs) if confs else 0.0
        if text:
            buckets[fk].append((text, mean_c))
        i = j if j > i else i + 1

    out: dict[str, tuple[Optional[str], float]] = {}
    for k in FIELD_KEYS:
        if not buckets[k]:
            out[k] = (None, 0.0)
        else:
            best = max(buckets[k], key=lambda x: len(x[0]))
            out[k] = (best[0], best[1])
    return out
