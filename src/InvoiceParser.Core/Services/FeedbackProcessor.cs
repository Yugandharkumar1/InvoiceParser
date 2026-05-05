using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services;

public class FeedbackProcessor
{
    private readonly RuleLearningService _learningService;
    private readonly ILogger<FeedbackProcessor> _logger;

    private static readonly Dictionary<string, string[]> FieldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["invoice_number"] = new[] { "invoice number", "invoice no", "invoice #", "bill number" },
        ["carrier_account"] = new[] { "account number", "account no", "account #", "account" },
        ["invoice_date"] = new[] { "invoice date", "bill date", "statement date" },
        ["invoice_st_dtm"] = new[] { "start date", "statement start", "period start", "service from", "billing period start" },
        ["invoice_end_dtm"] = new[] { "end date", "statement end", "period end", "service to", "billing period end" },
        ["invoice_due_dtm"] = new[] { "due date", "payment due", "pay by" },
        ["beg_bal"] = new[] { "previous balance", "beginning balance", "prior balance", "last bill", "previous bill" },
        ["payment"] = new[] { "payment", "payments", "payment received", "amount paid" },
        ["prev_adj"] = new[] { "previous adjustment", "prior adjustment", "adjustment" },
        ["curr_adj"] = new[] { "current adjustment" },
        ["curr_chg"] = new[] { "current charge", "current charges", "new charge", "new charges", "monthly charge" },
        ["curr_tax"] = new[] { "tax", "taxes", "fees", "surcharge", "surcharges", "taxes and fees" },
        ["end_bal"] = new[] { "balance due", "total due", "amount due", "ending balance", "total amount" },
    };

    private static readonly Regex CorrectValuePattern = new(
        @"(?:should\s+be|correct\s+(?:value|amount|number)\s+is|actual(?:ly)?\s+is|it\s+is|is\s+actually|change\s+(?:it\s+)?to|update\s+(?:it\s+)?to|set\s+(?:it\s+)?to)\s+\$?([\d,]+\.?\d*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CorrectDatePattern = new(
        @"(?:should\s+be|correct\s+(?:value|date)\s+is|actual(?:ly)?\s+is|it\s+is|is\s+actually|change\s+(?:it\s+)?to|update\s+(?:it\s+)?to|set\s+(?:it\s+)?to)\s+(\d{1,2}/\d{1,2}/\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CorrectTextPattern = new(
        @"(?:should\s+be|correct\s+(?:value|number)\s+is|actual(?:ly)?\s+is|it\s+is|is\s+actually|change\s+(?:it\s+)?to|update\s+(?:it\s+)?to|set\s+(?:it\s+)?to)\s+[""']?(\S+)[""']?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WrongValuePattern = new(
        @"(?:not|instead\s+of|got|captured|shows?|parsed\s+as)\s+\$?([\d,]+\.?\d*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> FieldToViewModelProperty = new()
    {
        ["invoice_number"] = "InvoiceNumber",
        ["carrier_account"] = "CarrierAccount",
        ["invoice_date"] = "InvoiceDate",
        ["invoice_st_dtm"] = "InvoiceStartDate",
        ["invoice_end_dtm"] = "InvoiceEndDate",
        ["invoice_due_dtm"] = "InvoiceDueDate",
        ["beg_bal"] = "BeginningBalance",
        ["payment"] = "Payment",
        ["prev_adj"] = "PreviousAdjustments",
        ["curr_adj"] = "CurrentAdjustments",
        ["curr_chg"] = "CurrentCharges",
        ["curr_tax"] = "CurrentTax",
        ["end_bal"] = "EndingBalance",
    };

    private static readonly HashSet<string> DateFields = new()
    {
        "invoice_date", "invoice_st_dtm", "invoice_end_dtm", "invoice_due_dtm"
    };

    private static readonly HashSet<string> TextFields = new()
    {
        "invoice_number", "carrier_account"
    };

    private static readonly Regex ChargeLinePattern = new(
        @"(?:charge|line|row)\s*(?:#|number|no\.?)?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionNoisePattern = new(
        @"(?:has\s+extra\s+text|extra\s+(?:word|text|value)|remove|strip|delete)\s+[""']?(.+?)[""']?\s*(?:\.|$|,)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MissingFieldPattern = new(
        @"(.+?)\s+(?:is\s+missing|not\s+(?:captured|detected|found|parsed|extracted)|was\s+not|wasn'?t)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WrongFieldPattern = new(
        @"(.+?)\s+(?:is\s+(?:wrong|incorrect|bad)|should\s+be|captured\s+wrong)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public FeedbackProcessor(RuleLearningService learningService, ILogger<FeedbackProcessor> logger)
    {
        _learningService = learningService;
        _logger = logger;
    }

    /// <summary>
    /// Extracts field corrections from feedback text.
    /// Returns a dictionary mapping ViewModel property names to corrected values.
    /// </summary>
    public Dictionary<string, string> ExtractCorrections(string feedbackText)
    {
        var corrections = new Dictionary<string, string>();
        var sentences = SplitIntoSentences(feedbackText);

        foreach (var sentence in sentences)
        {
            var field = IdentifyField(sentence);
            if (field == null) continue;

            if (!FieldToViewModelProperty.TryGetValue(field, out var propertyName))
                continue;

            string? correctedValue = null;

            if (DateFields.Contains(field))
            {
                var dateMatch = CorrectDatePattern.Match(sentence);
                if (dateMatch.Success)
                {
                    if (DateTime.TryParse(dateMatch.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt))
                    {
                        correctedValue = dt.ToString("yyyy-MM-dd");
                    }
                }
            }

            if (correctedValue == null && !TextFields.Contains(field))
            {
                var valueMatch = CorrectValuePattern.Match(sentence);
                if (valueMatch.Success)
                    correctedValue = valueMatch.Groups[1].Value.Replace(",", "").Trim();
            }

            if (correctedValue == null && TextFields.Contains(field))
            {
                var textMatch = CorrectTextPattern.Match(sentence);
                if (textMatch.Success)
                    correctedValue = textMatch.Groups[1].Value.Trim();
            }

            if (correctedValue == null)
            {
                var fallbackMatch = CorrectValuePattern.Match(sentence);
                if (fallbackMatch.Success)
                    correctedValue = fallbackMatch.Groups[1].Value.Replace(",", "").Trim();
            }

            if (!string.IsNullOrWhiteSpace(correctedValue) && !corrections.ContainsKey(propertyName))
            {
                corrections[propertyName] = correctedValue;
                _logger.LogInformation(
                    "Extracted correction from feedback: {Property} = '{Value}'",
                    propertyName, correctedValue);
            }
        }

        return corrections;
    }

    public List<VendorParsingRule> ProcessFeedback(InvoiceFeedback feedback)
    {
        var rules = new List<VendorParsingRule>();

        if (string.IsNullOrWhiteSpace(feedback.FeedbackText) || string.IsNullOrWhiteSpace(feedback.PdfText))
            return rules;

        var sentences = SplitIntoSentences(feedback.FeedbackText);

        Dictionary<string, string?>? confirmedFields = null;
        Dictionary<string, string?>? originalFields = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(feedback.ConfirmedFieldsJson))
                confirmedFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(feedback.ConfirmedFieldsJson);
            if (!string.IsNullOrWhiteSpace(feedback.OriginalFieldsJson))
                originalFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(feedback.OriginalFieldsJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize feedback JSON for FeedbackId {Id}", feedback.Id);
        }

        foreach (var sentence in sentences)
        {
            var fieldRules = ProcessFieldMention(sentence, feedback);
            rules.AddRange(fieldRules);

            var chargeRules = ProcessChargeMention(sentence, feedback);
            rules.AddRange(chargeRules);
        }

        // If the user corrected fields (confirmed != original), generate rules from those corrections
        if (confirmedFields != null && originalFields != null)
        {
            var correctionRules = ProcessFieldCorrections(
                feedback.PdfText, feedback.CarrierId, confirmedFields, originalFields, feedback.FeedbackText);
            rules.AddRange(correctionRules);
        }

        _logger.LogInformation(
            "Processed feedback {Id}: generated {Count} rules from text analysis.",
            feedback.Id, rules.Count);

        return rules;
    }

    private List<VendorParsingRule> ProcessFieldMention(string sentence, InvoiceFeedback feedback)
    {
        var rules = new List<VendorParsingRule>();
        var matchedField = IdentifyField(sentence);

        if (matchedField == null) return rules;

        var correctValueMatch = CorrectValuePattern.Match(sentence);
        if (!correctValueMatch.Success) return rules;

        var correctValue = correctValueMatch.Groups[1].Value.Replace(",", "").Trim();

        // Try to find this value in the PDF text and build a regex rule around it
        var generatedRules = _learningService.GenerateRules(
            feedback.PdfText!,
            new Dictionary<string, string?> { [matchedField] = correctValue },
            feedback.CarrierId);

        rules.AddRange(generatedRules);
        return rules;
    }

    private List<VendorParsingRule> ProcessChargeMention(string sentence, InvoiceFeedback feedback)
    {
        var rules = new List<VendorParsingRule>();

        var noiseMatch = DescriptionNoisePattern.Match(sentence);
        if (noiseMatch.Success)
        {
            var noiseText = noiseMatch.Groups[1].Value.Trim();
            if (noiseText.Length >= 3)
            {
                rules.Add(new VendorParsingRule
                {
                    CarrierId = feedback.CarrierId,
                    FieldName = "charge_skip_pattern",
                    RegexPattern = Regex.Escape(noiseText),
                    FieldType = "skip",
                    TargetTable = "t_charge",
                    SortOrder = 100,
                    IsActive = true,
                    SuccessCount = 1,
                });

                _logger.LogInformation("Created charge skip rule for noise: '{Noise}'", noiseText);
            }
        }

        return rules;
    }

    private List<VendorParsingRule> ProcessFieldCorrections(
        string pdfText, int carrierId,
        Dictionary<string, string?> confirmedFields,
        Dictionary<string, string?> originalFields,
        string feedbackText)
    {
        var rules = new List<VendorParsingRule>();
        var mentionedFields = new HashSet<string>();

        // Find which fields the feedback text mentions
        foreach (var sentence in SplitIntoSentences(feedbackText))
        {
            var field = IdentifyField(sentence);
            if (field != null) mentionedFields.Add(field);
        }

        // For mentioned fields where confirmed != original, generate better rules
        foreach (var fieldName in mentionedFields)
        {
            confirmedFields.TryGetValue(fieldName, out var confirmedVal);
            originalFields.TryGetValue(fieldName, out var originalVal);

            if (string.IsNullOrWhiteSpace(confirmedVal)) continue;

            var confNorm = NormalizeValue(confirmedVal);
            var origNorm = NormalizeValue(originalVal);

            if (confNorm != origNorm)
            {
                var generatedRules = _learningService.GenerateRules(
                    pdfText,
                    new Dictionary<string, string?> { [fieldName] = confirmedVal },
                    carrierId);

                rules.AddRange(generatedRules);
                _logger.LogInformation(
                    "Generated correction rule for field '{Field}': '{Orig}' -> '{Confirmed}'",
                    fieldName, originalVal, confirmedVal);
            }
        }

        return rules;
    }

    private static string? IdentifyField(string text)
    {
        foreach (var (fieldName, aliases) in FieldAliases)
        {
            foreach (var alias in aliases)
            {
                if (text.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    return fieldName;
            }
        }
        return null;
    }

    private static string[] SplitIntoSentences(string text)
    {
        return Regex.Split(text, @"(?<=[.!?\n])\s+")
            .Where(s => s.Trim().Length > 3)
            .Select(s => s.Trim())
            .ToArray();
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("$", "").Replace(",", "").Replace(" ", "").Trim().ToLowerInvariant();
    }
}
