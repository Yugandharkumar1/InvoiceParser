"""Tesseract OCR: words + pixel boxes + lines + sections (layout preserved)."""

from __future__ import annotations

import hashlib
import io
import json
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional

import fitz  # PyMuPDF
from PIL import Image
import pytesseract
from pytesseract import Output


@dataclass
class OcrWord:
    """Single word: pixel box + LayoutLM 0–1000 bbox (x0, y0, x1, y1)."""

    text: str
    bbox: tuple[float, float, float, float]  # normalized x0,y0,x1,y1
    x: float = 0.0
    y: float = 0.0
    width: float = 0.0
    height: float = 0.0
    conf: int = -1
    block_num: int = 0
    par_num: int = 0
    line_num: int = 0


@dataclass
class OcrLine:
    block_num: int
    par_num: int
    line_num: int
    text: str
    x: float
    y: float
    width: float
    height: float
    words: list[OcrWord] = field(default_factory=list)


@dataclass
class OcrSection:
    block_num: int
    text: str
    x: float
    y: float
    width: float
    height: float
    lines: list[OcrLine] = field(default_factory=list)


@dataclass
class OcrPage:
    page_index: int
    width_px: int
    height_px: int
    words: list[OcrWord]
    lines: list[OcrLine] = field(default_factory=list)
    sections: list[OcrSection] = field(default_factory=list)


def _normalize_box(
    left: float, top: float, w: float, h: float, page_w: float, page_h: float
) -> tuple[float, float, float, float]:
    sx = 1000.0 / max(page_w, 1.0)
    sy = 1000.0 / max(page_h, 1.0)
    x0 = max(0.0, min(1000.0, left * sx))
    y0 = max(0.0, min(1000.0, top * sy))
    x1 = max(0.0, min(1000.0, (left + w) * sx))
    y1 = max(0.0, min(1000.0, (top + h) * sy))
    return (x0, y0, x1, y1)


def _parse_tesseract_data(
    data: dict[str, Any], page_w: float, page_h: float, min_conf: int = 0
) -> list[OcrWord]:
    words: list[OcrWord] = []
    n = len(data.get("text", []))
    for i in range(n):
        txt = (data["text"][i] or "").strip()
        if not txt:
            continue
        try:
            conf = int(data["conf"][i])
        except (ValueError, KeyError):
            conf = -1
        if conf >= 0 and conf < min_conf:
            continue
        left = float(data["left"][i])
        top = float(data["top"][i])
        w = float(data["width"][i])
        h = float(data["height"][i])
        bbox = _normalize_box(left, top, w, h, page_w, page_h)
        try:
            bn = int(data["block_num"][i])
            pn = int(data["par_num"][i])
            ln = int(data["line_num"][i])
        except (KeyError, ValueError):
            bn, pn, ln = 0, 0, 0
        words.append(
            OcrWord(
                text=txt,
                bbox=bbox,
                x=left,
                y=top,
                width=w,
                height=h,
                conf=conf,
                block_num=bn,
                par_num=pn,
                line_num=ln,
            )
        )
    return words


def _group_words_to_lines(words: list[OcrWord]) -> list[OcrLine]:
    line_map: dict[tuple[int, int, int], list[OcrWord]] = defaultdict(list)
    for w in words:
        line_map[(w.block_num, w.par_num, w.line_num)].append(w)
    lines_out: list[OcrLine] = []
    for key in sorted(line_map.keys()):
        ws = sorted(line_map[key], key=lambda o: o.x)
        text = " ".join(o.text for o in ws)
        x0 = min(o.x for o in ws)
        y0 = min(o.y for o in ws)
        x1 = max(o.x + o.width for o in ws)
        y1 = max(o.y + o.height for o in ws)
        lines_out.append(
            OcrLine(
                block_num=key[0],
                par_num=key[1],
                line_num=key[2],
                text=text,
                x=x0,
                y=y0,
                width=max(0.0, x1 - x0),
                height=max(0.0, y1 - y0),
                words=ws,
            )
        )
    return lines_out


def _lines_to_sections(lines: list[OcrLine]) -> list[OcrSection]:
    by_block: dict[int, list[OcrLine]] = defaultdict(list)
    for ln in lines:
        by_block[ln.block_num].append(ln)
    sections: list[OcrSection] = []
    for bid in sorted(by_block.keys()):
        lns = sorted(by_block[bid], key=lambda l: (l.par_num, l.line_num))
        text = "\n".join(l.text for l in lns)
        x0 = min(l.x for l in lns)
        y0 = min(l.y for l in lns)
        x1 = max(l.x + l.width for l in lns)
        y1 = max(l.y + l.height for l in lns)
        sections.append(
            OcrSection(
                block_num=bid,
                text=text,
                x=x0,
                y=y0,
                width=max(0.0, x1 - x0),
                height=max(0.0, y1 - y0),
                lines=lns,
            )
        )
    return sections


def _finalize_page(page_index: int, width_px: int, height_px: int, words: list[OcrWord]) -> OcrPage:
    lines = _group_words_to_lines(words)
    sections = _lines_to_sections(lines)
    return OcrPage(
        page_index=page_index,
        width_px=width_px,
        height_px=height_px,
        words=words,
        lines=lines,
        sections=sections,
    )


def ocr_image_pil(
    image: Image.Image,
    lang: str = "eng",
    min_conf: int = 0,
    page_index: int = 0,
) -> tuple[int, int, OcrPage]:
    """Run Tesseract on a PIL image. Returns (width, height, OcrPage)."""
    w, h = image.size
    data = pytesseract.image_to_data(image, lang=lang, output_type=Output.DICT)
    words = _parse_tesseract_data(data, float(w), float(h), min_conf=min_conf)
    page = _finalize_page(page_index, w, h, words)
    return w, h, page


def ocr_pdf_bytes(
    pdf_bytes: bytes,
    lang: str = "eng",
    dpi_scale: float = 2.0,
) -> list[OcrPage]:
    """Render each PDF page to an image and OCR. Returns one OcrPage per page."""
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    pages: list[OcrPage] = []
    try:
        for i in range(len(doc)):
            page = doc.load_page(i)
            mat = fitz.Matrix(dpi_scale, dpi_scale)
            pix = page.get_pixmap(matrix=mat, alpha=False)
            mode = "RGB" if pix.n >= 3 else "L"
            img = Image.frombytes(mode, (pix.width, pix.height), pix.samples)
            _, _, ocr_page = ocr_image_pil(img, lang=lang, page_index=i)
            pages.append(ocr_page)
    finally:
        doc.close()
    return pages


def ocr_image_bytes(image_bytes: bytes, lang: str = "eng") -> list[OcrPage]:
    img = Image.open(io.BytesIO(image_bytes))
    img = img.convert("RGB")
    _, _, page = ocr_image_pil(img, lang=lang, page_index=0)
    return [page]


def word_to_json_dict(w: OcrWord) -> dict[str, Any]:
    return {
        "text": w.text,
        "x": w.x,
        "y": w.y,
        "width": w.width,
        "height": w.height,
        "confidence": w.conf,
        "bbox_norm": [w.bbox[0], w.bbox[1], w.bbox[2], w.bbox[3]],
    }


def line_to_json_dict(ln: OcrLine) -> dict[str, Any]:
    return {
        "text": ln.text,
        "x": ln.x,
        "y": ln.y,
        "width": ln.width,
        "height": ln.height,
        "block_num": ln.block_num,
        "par_num": ln.par_num,
        "line_num": ln.line_num,
        "words": [word_to_json_dict(w) for w in ln.words],
    }


def section_to_json_dict(sec: OcrSection, index: int) -> dict[str, Any]:
    return {
        "index": index,
        "block_num": sec.block_num,
        "text": sec.text,
        "x": sec.x,
        "y": sec.y,
        "width": sec.width,
        "height": sec.height,
        "lines": [line_to_json_dict(ln) for ln in sec.lines],
    }


def serialize_ocr_page_for_api(p: OcrPage) -> dict[str, Any]:
    """Structured JSON: words (pixel boxes), lines, sections — plus legacy words/bboxes for LayoutLM."""
    return {
        "page_index": p.page_index,
        "width_px": p.width_px,
        "height_px": p.height_px,
        "words": [w.text for w in p.words],
        "bboxes": [[w.bbox[0], w.bbox[1], w.bbox[2], w.bbox[3]] for w in p.words],
        "words_detailed": [word_to_json_dict(w) for w in p.words],
        "lines": [line_to_json_dict(ln) for ln in p.lines],
        "sections": [section_to_json_dict(sec, i) for i, sec in enumerate(p.sections)],
    }


def flatten_pages_to_words(pages: list[OcrPage]) -> tuple[list[str], list[list[float]]]:
    """Flatten to word list and bbox list for LayoutLM."""
    texts: list[str] = []
    boxes: list[list[float]] = []
    for p in pages:
        for w in p.words:
            texts.append(w.text)
            boxes.append([w.bbox[0], w.bbox[1], w.bbox[2], w.bbox[3]])
    return texts, boxes


def full_text_from_pages(pages: list[OcrPage]) -> str:
    parts: list[str] = []
    for p in pages:
        if p.lines:
            parts.append("\n".join(ln.text for ln in p.lines))
        else:
            parts.append(" ".join(w.text for w in p.words))
    return "\n\n".join(parts)


def cache_key(file_bytes: bytes, lang: str) -> str:
    h = hashlib.sha256(file_bytes).hexdigest()
    return f"{h}_{lang}"


def load_cached_ocr(cache_dir: Path, key: str) -> Optional[list[dict[str, Any]]]:
    path = cache_dir / f"{key}.json"
    if not path.is_file():
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save_cached_ocr(cache_dir: Path, key: str, pages: list[OcrPage]) -> None:
    cache_dir.mkdir(parents=True, exist_ok=True)
    ser = []
    for p in pages:
        ser.append(
            {
                "page_index": p.page_index,
                "width_px": p.width_px,
                "height_px": p.height_px,
                "words": [_word_to_cache_row(w) for w in p.words],
            }
        )
    with open(cache_dir / f"{key}.json", "w", encoding="utf-8") as f:
        json.dump(ser, f)


def _word_to_cache_row(w: OcrWord) -> dict[str, Any]:
    return {
        "text": w.text,
        "bbox": list(w.bbox),
        "x": w.x,
        "y": w.y,
        "width": w.width,
        "height": w.height,
        "conf": w.conf,
        "block_num": w.block_num,
        "par_num": w.par_num,
        "line_num": w.line_num,
    }


def pages_from_cached(ser: list[dict[str, Any]]) -> list[OcrPage]:
    out: list[OcrPage] = []
    for row in ser:
        words = [_word_from_cache(w) for w in row["words"]]
        out.append(
            _finalize_page(
                int(row["page_index"]),
                int(row["width_px"]),
                int(row["height_px"]),
                words,
            )
        )
    return out


def _word_from_cache(w: dict[str, Any]) -> OcrWord:
    bbox = tuple(float(x) for x in w["bbox"])  # type: ignore[misc]
    return OcrWord(
        text=str(w["text"]),
        bbox=bbox,
        x=float(w.get("x", 0)),
        y=float(w.get("y", 0)),
        width=float(w.get("width", 0)),
        height=float(w.get("height", 0)),
        conf=int(w.get("conf", -1)),
        block_num=int(w.get("block_num", 0)),
        par_num=int(w.get("par_num", 0)),
        line_num=int(w.get("line_num", 0)),
    )
