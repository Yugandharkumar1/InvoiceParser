using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services;
using InvoiceParser.Web.Configuration;
using Microsoft.Extensions.Options;

namespace InvoiceParser.Web.Services;

/// <summary>
/// Calls the Python hybrid parser to fill gaps in <see cref="ParsedInvoiceResult"/> after the core C# pipeline.
/// </summary>
public sealed class PythonInvoiceIntegrationService : IInvoicePythonAugmenter
{
    public const string HttpClientName = "InvoiceParserPython";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly InvoiceParserPythonSettings _settings;
    private readonly ILogger<PythonInvoiceIntegrationService> _logger;

    public PythonInvoiceIntegrationService(
        IHttpClientFactory httpFactory,
        IOptions<InvoiceParserPythonSettings> settings,
        ILogger<PythonInvoiceIntegrationService> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsEnabled => _settings.Enabled && Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out _);

    /// <inheritdoc />
    public async Task TryAugmentParseAsync(
        byte[] fileBytes,
        string fileName,
        string? vendorKey,
        ParsePdfResult result,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || fileBytes.Length == 0)
            return;

        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(new MemoryStream(fileBytes));
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "file", fileName);
            if (!string.IsNullOrWhiteSpace(vendorKey))
                content.Add(new StringContent(vendorKey), "vendor_key");

            using var response = await client.PostAsync("parse", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Python parser returned {Status}: {Body}", (int)response.StatusCode, err);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<PythonParseResponseDto>(json, JsonOptions);
            if (dto == null)
                return;

            var parsed = result.Parsed;
            var valid = new HashSet<string>
            {
                "invoice_number", "carrier_account", "invoice_date",
                "invoice_st_dtm", "invoice_end_dtm", "invoice_due_dtm",
                "beg_bal", "payment", "prev_adj", "curr_adj",
                "curr_chg", "curr_tax", "end_bal"
            };

            void FillGap(string key, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var existing = parsed.SummaryFields.GetValueOrDefault(key);
                if (!string.IsNullOrWhiteSpace(existing)) return;
                if (valid.Contains(key))
                    parsed.SummaryFields[key] = value.Trim();
            }

            FillGap("invoice_number", dto.InvoiceNo);
            FillGap("invoice_date", dto.Date);
            // "total" from Python ~ amount due → prefer end_bal, then curr_chg
            if (!string.IsNullOrWhiteSpace(dto.Total))
            {
                var t = dto.Total.Trim();
                if (string.IsNullOrWhiteSpace(parsed.SummaryFields.GetValueOrDefault("end_bal")))
                    FillGap("end_bal", t);
                else if (string.IsNullOrWhiteSpace(parsed.SummaryFields.GetValueOrDefault("curr_chg")))
                    FillGap("curr_chg", t);
            }

            var ocrJson = dto.Ocr != null ? JsonSerializer.Serialize(dto.Ocr, JsonOptions) : null;
            result.Python = new PythonParseTrace
            {
                ParseId = dto.ParseId,
                Source = dto.Source,
                OcrJson = ocrJson,
                Confidence = dto.Confidence,
                PerFieldSource = dto.PerFieldSource,
                VendorHint = dto.Vendor,
            };

            _logger.LogInformation("Python parser augmented parse: source={Source}, parse_id={Id}",
                dto.Source, dto.ParseId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python parser call failed; continuing with C# results only.");
        }
    }

    /// <summary>POST /feedback for LayoutLM training dataset (tokens, bboxes, full corrected summary).</summary>
    public async Task TrySubmitFeedbackAsync(
        Invoice savedInvoice,
        PythonParseTrace? trace,
        IReadOnlyDictionary<string, string?> confirmedFields,
        string? vendorDisplay = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || !_settings.SubmitFeedbackOnSave || trace == null || string.IsNullOrEmpty(trace.OcrJson))
            return;

        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            List<PythonOcrPageDto>? pages = null;
            try
            {
                var ocr = JsonSerializer.Deserialize<PythonOcrPayloadDto>(trace.OcrJson!, JsonOptions);
                pages = ocr?.Pages;
            }
            catch (JsonException)
            {
                return;
            }

            var body = new PythonFeedbackRequestDto
            {
                InvoiceId = savedInvoice.Id.ToString(),
                CorrectedFields = BuildMlFeedbackDictionary(confirmedFields, vendorDisplay, trace.VendorHint),
                Pages = pages,
            };

            var json = JsonSerializer.Serialize(body, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, "feedback")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Python feedback POST failed: {Status}", (int)resp.StatusCode);
                return;
            }

            _logger.LogInformation("Python ML feedback submitted for invoice Id={Id} (fields={Count}, pages={Pg})",
                savedInvoice.Id, body.CorrectedFields.Count, pages?.Count ?? 0);

            if (_settings.TriggerRetrainAfterFeedback)
            {
                try
                {
                    using var retrainResp = await client
                        .PostAsync("admin/retrain", null, cancellationToken)
                        .ConfigureAwait(false);
                    if (retrainResp.IsSuccessStatusCode)
                        _logger.LogInformation("Python admin/retrain invoked after feedback for invoice Id={Id}", savedInvoice.Id);
                    else
                        _logger.LogWarning("Python admin/retrain returned {Status}", (int)retrainResp.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Python admin/retrain call failed after feedback.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python feedback submission failed.");
        }
    }

    /// <summary>
    /// Full summary fields for dataset.json plus canonical keys (invoice_no, date, total, vendor) for weak labeling.
    /// </summary>
    internal static Dictionary<string, string?> BuildMlFeedbackDictionary(
        IReadOnlyDictionary<string, string?> confirmedFields,
        string? vendorDisplay,
        string? vendorHint)
    {
        var f = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in confirmedFields)
        {
            if (string.IsNullOrWhiteSpace(kv.Value))
                continue;
            f[kv.Key] = kv.Value!.Trim();
        }

        void EnsureAlias(string canonical, params string[] sourceKeys)
        {
            if (f.TryGetValue(canonical, out var existing) && !string.IsNullOrWhiteSpace(existing))
                return;
            foreach (var k in sourceKeys)
            {
                if (!f.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v))
                    continue;
                f[canonical] = v.Trim();
                return;
            }
        }

        EnsureAlias("invoice_no", "invoice_number");
        EnsureAlias("date", "invoice_date");
        EnsureAlias("total", "total", "end_bal", "curr_chg", "beg_bal");
        EnsureAlias("vendor", "vendor");
        if (!f.TryGetValue("vendor", out var vend) || string.IsNullOrWhiteSpace(vend))
        {
            if (!string.IsNullOrWhiteSpace(vendorDisplay))
                f["vendor"] = vendorDisplay.Trim();
            else if (!string.IsNullOrWhiteSpace(vendorHint))
                f["vendor"] = vendorHint.Trim();
        }

        return f;
    }
}
