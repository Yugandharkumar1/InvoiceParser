using System.Text.Json.Serialization;

namespace InvoiceParser.Web.Services;

internal sealed class PythonParseResponseDto
{
    [JsonPropertyName("invoice_no")]
    public string? InvoiceNo { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("total")]
    public string? Total { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("confidence")]
    public Dictionary<string, float>? Confidence { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("per_field_source")]
    public Dictionary<string, string>? PerFieldSource { get; set; }

    [JsonPropertyName("ocr")]
    public PythonOcrPayloadDto? Ocr { get; set; }

    [JsonPropertyName("parse_id")]
    public string? ParseId { get; set; }
}

internal sealed class PythonOcrPayloadDto
{
    [JsonPropertyName("pages")]
    public List<PythonOcrPageDto>? Pages { get; set; }
}

internal sealed class PythonOcrPageDto
{
    [JsonPropertyName("words")]
    public List<string> Words { get; set; } = new();

    [JsonPropertyName("bboxes")]
    public List<List<float>> Bboxes { get; set; } = new();
}

internal sealed class PythonFeedbackRequestDto
{
    [JsonPropertyName("invoice_id")]
    public string InvoiceId { get; set; } = "";

    [JsonPropertyName("corrected_fields")]
    public Dictionary<string, string?> CorrectedFields { get; set; } = new();

    [JsonPropertyName("pages")]
    public List<PythonOcrPageDto>? Pages { get; set; }
}
