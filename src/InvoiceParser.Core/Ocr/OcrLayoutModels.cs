using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceParser.Core.Ocr;

/// <summary>Single OCR word with pixel-space box (Tesseract coordinates, top-left origin).</summary>
public sealed class OcrWordBox
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("confidence")]
    public float Confidence { get; set; }

    /// <summary>LayoutLM-style box: x0, y0, x1, y1 on 0–1000 scale.</summary>
    [JsonPropertyName("bbox_norm")]
    public float[] BboxNorm { get; set; } = Array.Empty<float>();
}

/// <summary>One text line (Tesseract textline), words left-to-right.</summary>
public sealed class OcrLineLayout
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("block_index")]
    public int BlockIndex { get; set; }

    [JsonPropertyName("line_index")]
    public int LineIndex { get; set; }

    [JsonPropertyName("words")]
    public List<OcrWordBox> Words { get; set; } = new();
}

/// <summary>Layout block (Tesseract block): paragraph/column region.</summary>
public sealed class OcrSectionLayout
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("lines")]
    public List<OcrLineLayout> Lines { get; set; } = new();
}

/// <summary>Structured OCR for one rendered page image.</summary>
public sealed class OcrStructuredPage
{
    [JsonPropertyName("page_index")]
    public int PageIndex { get; set; }

    [JsonPropertyName("width_px")]
    public int WidthPx { get; set; }

    [JsonPropertyName("height_px")]
    public int HeightPx { get; set; }

    [JsonPropertyName("words")]
    public List<OcrWordBox> Words { get; set; } = new();

    [JsonPropertyName("lines")]
    public List<OcrLineLayout> Lines { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<OcrSectionLayout> Sections { get; set; } = new();
}

public sealed class OcrStructuredDocument
{
    [JsonPropertyName("pages")]
    public List<OcrStructuredPage> Pages { get; set; } = new();
}

public static class OcrStructuredPageJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ToJson(OcrStructuredPage page) =>
        JsonSerializer.Serialize(page, Options);

    public static string ToJson(IReadOnlyList<OcrStructuredPage> pages) =>
        JsonSerializer.Serialize(new OcrStructuredDocument { Pages = pages.ToList() }, Options);
}
