using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services.ML;

public class FeedbackTrainingDataService : IFeedbackTrainingDataService
{
    private readonly IInvoiceRepository _repository;
    private readonly ILogger<FeedbackTrainingDataService> _logger;

    private static readonly HashSet<string> SummaryFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoice_number", "carrier_account", "invoice_date",
        "invoice_due_dtm", "invoice_st_dtm", "invoice_end_dtm",
        "beg_bal", "payment", "prev_adj", "curr_adj",
        "curr_chg", "curr_tax", "end_bal"
    };

    public FeedbackTrainingDataService(IInvoiceRepository repository, ILogger<FeedbackTrainingDataService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<int> ExportFeedbackToCsvAsync(string outputFolder)
    {
        var feedbackItems = await _repository.GetUnprocessedFeedbackAsync();
        if (feedbackItems.Count == 0)
        {
            _logger.LogInformation("No unprocessed feedback to export.");
            return 0;
        }

        _logger.LogInformation("Exporting {Count} feedback item(s) to CSV...", feedbackItems.Count);

        var rows = new List<LineClassificationData>();

        foreach (var fb in feedbackItems)
        {
            if (string.IsNullOrWhiteSpace(fb.PdfText))
                continue;

            var summaryValues = ExtractSummaryValues(fb.ConfirmedFieldsJson);
            var chargeValues = ExtractChargeValues(fb.OriginalChargesJson);
            var pdfLines = fb.PdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in pdfLines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.Length < 3)
                    continue;

                var label = ClassifyLine(line, summaryValues, chargeValues);
                rows.Add(new LineClassificationData { Text = SanitizeCsvField(line), Label = label });
            }
        }

        if (rows.Count == 0)
        {
            _logger.LogWarning("No labeled rows generated from feedback.");
            return 0;
        }

        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var filePath = Path.Combine(outputFolder, $"feedback_{timestamp}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Text,Label");
        foreach (var row in rows)
            sb.AppendLine($"{row.Text},{row.Label}");

        await File.WriteAllTextAsync(filePath, sb.ToString());
        _logger.LogInformation("Exported {Count} rows to {Path}", rows.Count, filePath);

        var processedIds = feedbackItems.Select(f => f.Id).ToList();
        await _repository.MarkFeedbackBatchProcessedAsync(processedIds);
        _logger.LogInformation("Marked {Count} feedback item(s) as processed.", processedIds.Count);

        return rows.Count;
    }

    private static string ClassifyLine(string line, HashSet<string> summaryValues, HashSet<string> chargeValues)
    {
        foreach (var val in summaryValues)
        {
            if (line.Contains(val, StringComparison.OrdinalIgnoreCase))
                return "Summary";
        }

        foreach (var val in chargeValues)
        {
            if (line.Contains(val, StringComparison.OrdinalIgnoreCase))
                return "Charges";
        }

        return "Ignore";
    }

    private static HashSet<string> ExtractSummaryValues(string? confirmedFieldsJson)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(confirmedFieldsJson))
            return values;

        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, string?>>(confirmedFieldsJson);
            if (fields == null) return values;

            foreach (var (key, val) in fields)
            {
                if (!SummaryFields.Contains(key) || string.IsNullOrWhiteSpace(val))
                    continue;

                values.Add(val);

                if (decimal.TryParse(val.Replace("$", "").Replace(",", ""),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
                {
                    values.Add(dec.ToString("F2", CultureInfo.InvariantCulture));
                    values.Add(dec.ToString("N2", CultureInfo.InvariantCulture));
                }
            }
        }
        catch { /* malformed JSON, skip */ }

        return values;
    }

    private static HashSet<string> ExtractChargeValues(string? chargesJson)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(chargesJson))
            return values;

        try
        {
            using var doc = JsonDocument.Parse(chargesJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("Description", out var desc))
                {
                    var descStr = desc.GetString();
                    if (!string.IsNullOrWhiteSpace(descStr))
                        values.Add(descStr);
                }
            }
        }
        catch { /* malformed JSON, skip */ }

        return values;
    }

    private static string SanitizeCsvField(string field)
    {
        var sanitized = field.Replace("\"", "\"\"");
        if (sanitized.Contains(',') || sanitized.Contains('"') || sanitized.Contains('\n'))
            return $"\"{sanitized}\"";
        return sanitized;
    }
}
