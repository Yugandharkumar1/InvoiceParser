using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;

namespace InvoiceParser.Core.Services.ML;

public class TrainingDataBuilder
{
    private static readonly Dictionary<string, Func<Invoice, string?>> FieldExtractors = new()
    {
        ["invoice_number"] = inv => inv.InvoiceNumber,
        ["carrier_account"] = inv => inv.CarrierAccount,
        ["invoice_date"] = inv => inv.InvoiceDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        ["invoice_due_dtm"] = inv => inv.InvoiceDueDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        ["invoice_st_dtm"] = inv => inv.InvoiceStartDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        ["invoice_end_dtm"] = inv => inv.InvoiceEndDate?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        ["beg_bal"] = inv => inv.BeginningBalance?.ToString("F2", CultureInfo.InvariantCulture),
        ["payment"] = inv => inv.Payment?.ToString("F2", CultureInfo.InvariantCulture),
        ["prev_adj"] = inv => inv.PreviousAdjustments?.ToString("F2", CultureInfo.InvariantCulture),
        ["curr_adj"] = inv => inv.CurrentAdjustments?.ToString("F2", CultureInfo.InvariantCulture),
        ["curr_chg"] = inv => inv.CurrentCharges?.ToString("F2", CultureInfo.InvariantCulture),
        ["curr_tax"] = inv => inv.CurrentTax?.ToString("F2", CultureInfo.InvariantCulture),
        ["end_bal"] = inv => inv.EndingBalance?.ToString("F2", CultureInfo.InvariantCulture),
    };

    private const int ContextWindowSize = 120;

    public List<FieldTrainingData> BuildFromInvoices(List<Invoice> invoices)
    {
        var samples = new List<FieldTrainingData>();
        var random = new Random(42);

        foreach (var invoice in invoices)
        {
            if (string.IsNullOrWhiteSpace(invoice.PdfText)) continue;

            var foundPositions = new HashSet<int>();

            // Summary field samples
            foreach (var (fieldName, extractor) in FieldExtractors)
            {
                var value = extractor(invoice);
                if (string.IsNullOrWhiteSpace(value)) continue;

                var contexts = FindAllContexts(invoice.PdfText, value);
                foreach (var (context, position) in contexts)
                {
                    samples.Add(new FieldTrainingData
                    {
                        Context = NormalizeContext(context),
                        Label = fieldName,
                    });
                    foundPositions.Add(position);
                }
            }

            // Charge-level samples: teach the model to recognize charge descriptions and amounts
            if (invoice.Charges != null)
            {
                foreach (var charge in invoice.Charges)
                {
                    if (!string.IsNullOrWhiteSpace(charge.ChargeDescription))
                    {
                        var descContexts = FindAllContexts(invoice.PdfText, charge.ChargeDescription);
                        foreach (var (context, position) in descContexts)
                        {
                            samples.Add(new FieldTrainingData
                            {
                                Context = NormalizeContext(context),
                                Label = "charge_description",
                            });
                            foundPositions.Add(position);
                        }
                    }

                    if (charge.Amount.HasValue)
                    {
                        var amtStr = charge.Amount.Value.ToString("F2", CultureInfo.InvariantCulture);
                        var amtContexts = FindAllContexts(invoice.PdfText, amtStr);
                        foreach (var (context, position) in amtContexts)
                        {
                            samples.Add(new FieldTrainingData
                            {
                                Context = NormalizeContext(context),
                                Label = "charge_amount",
                            });
                            foundPositions.Add(position);
                        }
                    }
                }
            }

            var negatives = GenerateNegativeExamples(invoice.PdfText, foundPositions, random);
            samples.AddRange(negatives);
        }

        return samples;
    }

    public List<FieldTrainingData> BuildFromFeedback(List<InvoiceFeedback> feedbackRecords)
    {
        var samples = new List<FieldTrainingData>();

        foreach (var fb in feedbackRecords)
        {
            if (string.IsNullOrWhiteSpace(fb.PdfText) || string.IsNullOrWhiteSpace(fb.ConfirmedFieldsJson))
                continue;

            Dictionary<string, string?>? confirmedFields = null;
            try
            {
                confirmedFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(fb.ConfirmedFieldsJson);
            }
            catch { continue; }

            if (confirmedFields == null) continue;

            var foundPositions = new HashSet<int>();

            foreach (var (fieldName, value) in confirmedFields)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                var contexts = FindAllContexts(fb.PdfText, value);
                foreach (var (context, position) in contexts)
                {
                    samples.Add(new FieldTrainingData
                    {
                        Context = NormalizeContext(context),
                        Label = fieldName,
                    });
                    foundPositions.Add(position);
                }
            }

            var negatives = GenerateNegativeExamples(fb.PdfText, foundPositions, new Random(fb.Id));
            samples.AddRange(negatives);
        }

        return samples;
    }

    private static List<(string context, int position)> FindAllContexts(string pdfText, string value)
    {
        var results = new List<(string, int)>();
        var cleanedValue = value.Replace(",", "");

        var searchVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value };
        if (value != cleanedValue)
            searchVariants.Add(cleanedValue);

        if (decimal.TryParse(cleanedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
        {
            searchVariants.Add(dec.ToString("N2", CultureInfo.InvariantCulture));
            searchVariants.Add(dec.ToString("F2", CultureInfo.InvariantCulture));
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            searchVariants.Add(dt.ToString("M/d/yyyy"));
            searchVariants.Add(dt.ToString("MM/dd/yyyy"));
            searchVariants.Add(dt.ToString("M/dd/yyyy"));
            searchVariants.Add(dt.ToString("MM/d/yyyy"));
            searchVariants.Add(dt.ToString("MMMM d, yyyy"));
            searchVariants.Add(dt.ToString("MMM d, yyyy"));
            searchVariants.Add(dt.ToString("MMMM dd, yyyy"));
        }

        foreach (var variant in searchVariants)
        {
            var idx = pdfText.IndexOf(variant, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && !results.Any(r => Math.Abs(r.Item2 - idx) < 10))
            {
                var context = ExtractContextWindow(pdfText, idx, variant.Length);
                results.Add((context, idx));
            }
        }

        return results;
    }

    private static string ExtractContextWindow(string text, int valueStart, int valueLength)
    {
        var start = Math.Max(0, valueStart - ContextWindowSize);
        var end = Math.Min(text.Length, valueStart + valueLength + ContextWindowSize);
        return text[start..end];
    }

    private static List<FieldTrainingData> GenerateNegativeExamples(
        string pdfText, HashSet<int> positivePositions, Random random)
    {
        var negatives = new List<FieldTrainingData>();
        var lines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var targetCount = Math.Max(positivePositions.Count * 2, 10);

        var candidateLines = lines
            .Select((line, idx) => (line: line.Trim(), idx))
            .Where(x => x.line.Length >= 5 && x.line.Length < 200)
            .ToList();

        if (candidateLines.Count == 0) return negatives;

        for (int i = 0; i < targetCount && i < candidateLines.Count * 2; i++)
        {
            var pick = candidateLines[random.Next(candidateLines.Count)];
            var lineStart = pdfText.IndexOf(pick.line, StringComparison.Ordinal);

            if (lineStart >= 0 && !positivePositions.Any(p => Math.Abs(p - lineStart) < ContextWindowSize))
            {
                var context = ExtractContextWindow(pdfText, lineStart, pick.line.Length);
                negatives.Add(new FieldTrainingData
                {
                    Context = NormalizeContext(context),
                    Label = "none",
                });
            }
        }

        return negatives;
    }

    private static string NormalizeContext(string context)
    {
        var normalized = Regex.Replace(context, @"\s+", " ").Trim();
        return normalized.Length > ContextWindowSize * 3
            ? normalized[..(ContextWindowSize * 3)]
            : normalized;
    }
}
