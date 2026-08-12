using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Parsing;

namespace InvoiceParser.Core.Services;

public class ParsedInvoiceResult
{
    public Dictionary<string, string?> SummaryFields { get; set; } = new();
    public List<ParsedCharge> Charges { get; set; } = new();
    public List<ParsedUsageItem> Usages { get; set; } = new();
    public List<ParsedInventoryItem> Inventories { get; set; } = new();
}

public class ParsedCharge
{
    public string? ChargeDescription { get; set; }
    public decimal? Amount { get; set; }
    public string? Line { get; set; }
    public string? Location { get; set; }
    /// <summary>
    /// Zero-based index of the line in the PDF text array that triggered this charge.
    /// Used by DeduplicateCharges to distinguish legitimately identical charges
    /// (e.g. same service billed to two different accounts on the same invoice)
    /// from true extraction duplicates (same line captured by two code paths).
    /// </summary>
    public int SourceLineIndex { get; set; } = -1;
}

public class ParsedUsageItem
{
    public string? LineNumber { get; set; }
    public string? EmployeeName { get; set; }
    public string? UsocName { get; set; }
    public string? UsageLimit { get; set; }
    public string? UsageAmount { get; set; }
    public string? Charge { get; set; }
    public string? UsageType { get; set; }
}

public class ParsedInventoryItem
{
    public string? ReferenceNumber { get; set; }
    public string? EmployeeName { get; set; }
    public string? PlanName { get; set; }
    public string? PlanAmount { get; set; }
    public string? ServiceType { get; set; }
}

public class GenericInvoiceParser
{
    public ParsedInvoiceResult Parse(string pdfText, IEnumerable<VendorParsingRule> rules)
        => Parse(pdfText, rules, null);

    /// <param name="carrierCode">Optional carrier code for <see cref="VendorParsingPluginRegistry"/> plugins.</param>
    public ParsedInvoiceResult Parse(string pdfText, IEnumerable<VendorParsingRule> rules, string? carrierCode)
    {
        var activeRules = rules.Where(r => r.IsActive)
            .OrderByDescending(r => r.SuccessCount - r.FailCount)
            .ThenBy(r => r.SortOrder)
            .ToList();

        // All active t_charge rules are forwarded to ChargeExtractor which categorises
        // them internally (anchors, section_start, section_end, skip, cleanup transforms).
        var allChargeRules = activeRules
            .Where(r => r.TargetTable == "t_charge")
            .ToList();

        // learnedSkipPatterns is still used by CleanChargeDescriptions for the Verizon path
        var learnedSkipPatterns = allChargeRules
            .Where(r => r.FieldType == "skip")
            .Select(r => r.RegexPattern)
            .ToList();

        var normalized = InvoiceTextNormalizer.Normalize(pdfText);
        var result = SmartParse(normalized, allChargeRules);

        VendorParsingPluginRegistry.ApplyAll(carrierCode, normalized, result);

        ApplyLearnedRulesOverlay(normalized, result, activeRules);

        if (learnedSkipPatterns.Count > 0)
            CleanChargeDescriptions(result, learnedSkipPatterns);

        ChargeExtractor.DeduplicateCharges(result.Charges);
        AssociateCircuitIds(normalized, result);
        return result;
    }

    /// <summary>
    /// Applies learned VendorParsingRules on top of an existing parse result.
    /// Rules override any previously extracted values for the same field.
    /// Supports both legacy regex-only rules and the new configurable condition/transform rules.
    /// </summary>
    public void ApplyLearnedRulesOverlay(string pdfText, ParsedInvoiceResult result,
        IEnumerable<VendorParsingRule> rules)
    {
        var activeRules = rules.Where(r => r.IsActive)
            .OrderByDescending(r => r.SuccessCount - r.FailCount)
            .ThenBy(r => r.SortOrder)
            .ToList();

        var summaryRules = activeRules
            .Where(r => r.TargetTable == "t_invoice" && r.FieldType != "skip")
            .ToList();

        var textLines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rule in summaryRules)
        {
            try
            {
                string? value    = null;
                string? matchedLine = null;

                if (IsRegexCondition(rule.ConditionType))
                {
                    // regex_match: scan the full text for the pattern.
                    // Use capture group 1 when present; fall back to the full match so that
                    // patterns without explicit groups still produce a value.
                    if (string.IsNullOrWhiteSpace(rule.RegexPattern)) continue;
                    // Use Multiline so ^ / $ anchor to line boundaries and "." does NOT
                    // cross line endings. This prevents (.+) style capture groups from
                    // greedily consuming the rest of the entire document.
                    var match = Regex.Match(pdfText, rule.RegexPattern,
                        RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        value       = (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).Trim();
                        matchedLine = match.Value.Trim();
                    }
                }
                else
                {
                    // Structured condition: evaluate per line, use the matching line as the raw value.
                    foreach (var line in textLines)
                    {
                        var trimmed = line.Trim();
                        if (!ConditionEvaluator.Evaluate(trimmed, rule.ConditionType, rule.RegexPattern))
                            continue;
                        value       = trimmed;
                        matchedLine = trimmed;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(value)) continue;

                // Apply transform pipeline — forward pdfText + matchedLine for join_next/prev_line
                var steps = TransformPipeline.Deserialize(rule.TransformationsJson);
                if (steps != null)
                    value = TransformPipeline.Apply(value, steps, pdfText, matchedLine);

                if (!string.IsNullOrWhiteSpace(value))
                    result.SummaryFields[rule.FieldName] = value;
            }
            catch { /* skip invalid rule */ }
        }

        // ── t_charge rules ────────────────────────────────────────────────────
        // Two distinct behaviours depending on whether the rule has transforms:
        //
        //  A) CLEANUP path  — rule has TransformationsJson
        //     Applies the transform pipeline to already-extracted charge descriptions
        //     where the condition matches.  This is what wizard-created rules produce
        //     (e.g. regex_remove to strip dates from descriptions).
        //
        //  B) ADD-NEW-CHARGES path — rule has no transforms
        //     Legacy behaviour: the RegexPattern must contain two capture groups
        //     (description + amount) and is applied against the full PDF text to inject
        //     extra charge rows that the built-in extractor missed.
        //
        // The overlay only processes value-extraction rules (string/decimal/date transforms).
        // Boundary markers (section_start, section_end) and structural rules (anchors, skip)
        // are handled entirely inside ChargeExtractor.Extract and must be excluded here.
        var chargeRules = activeRules
            .Where(r => r.TargetTable == "t_charge"
                     && r.FieldType != "skip"
                     && r.FieldType != "line_anchor"  && r.FieldType != "location_anchor"
                     && r.FieldType != "section_start" && r.FieldType != "section_end"
                     && r.FieldName != "line_number"  && r.FieldName != "location")
            .ToList();

        foreach (var rule in chargeRules)
        {
            try
            {
                var steps = TransformPipeline.Deserialize(rule.TransformationsJson);

                // ── A: Cleanup path ──────────────────────────────────────────
                if (steps != null && steps.Count > 0)
                {
                    foreach (var charge in result.Charges)
                    {
                        if (string.IsNullOrWhiteSpace(charge.ChargeDescription)) continue;

                        // Evaluate the condition against the extracted charge description.
                        bool conditionMet;
                        if (IsRegexCondition(rule.ConditionType))
                        {
                            // An empty pattern means "apply to all"
                            conditionMet = string.IsNullOrWhiteSpace(rule.RegexPattern)
                                || Regex.IsMatch(charge.ChargeDescription,
                                       rule.RegexPattern, RegexOptions.IgnoreCase);
                        }
                        else
                        {
                            conditionMet = ConditionEvaluator.Evaluate(
                                charge.ChargeDescription, rule.ConditionType, rule.RegexPattern);
                        }

                        if (!conditionMet) continue;

                        var cleaned = TransformPipeline.Apply(
                            charge.ChargeDescription, steps,
                            pdfText, charge.ChargeDescription);

                        if (!string.IsNullOrWhiteSpace(cleaned))
                            charge.ChargeDescription = cleaned;
                    }
                    continue; // done with this rule — do not fall through to ADD path
                }

                // ── B: Add-new-charges path (legacy) ────────────────────────
                if (string.IsNullOrWhiteSpace(rule.RegexPattern)) continue;
                var rawMatches = Regex.Matches(pdfText, rule.RegexPattern,
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                foreach (Match match in rawMatches)
                {
                    // Require at least two capture groups: description (1) + amount (2)
                    if (match.Groups.Count < 3) continue;

                    var charge = new ParsedCharge
                    {
                        ChargeDescription = match.Groups[1].Value.Trim(),
                    };

                    if (decimal.TryParse(
                            match.Groups[2].Value.Trim().Replace("$", "").Replace(",", ""),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                    {
                        charge.Amount = amount;
                    }

                    result.Charges.Add(charge);
                }
            }
            catch { /* skip invalid rule */ }
        }
    }

    private static bool IsRegexCondition(string? conditionType)
        => string.IsNullOrEmpty(conditionType) ||
           conditionType.Equals("regex_match", StringComparison.OrdinalIgnoreCase);

    private static void CleanChargeDescriptions(ParsedInvoiceResult result, List<string> skipPatterns)
    {
        var toRemove = new List<ParsedCharge>();

        foreach (var charge in result.Charges)
        {
            if (string.IsNullOrWhiteSpace(charge.ChargeDescription)) continue;

            var desc = charge.ChargeDescription;
            foreach (var pattern in skipPatterns)
            {
                desc = Regex.Replace(desc, pattern, "", RegexOptions.IgnoreCase).Trim();
            }

            if (string.IsNullOrWhiteSpace(desc) || desc.Length < 3)
                toRemove.Add(charge);
            else
                charge.ChargeDescription = desc;
        }

        foreach (var bad in toRemove)
            result.Charges.Remove(bad);
    }

    private static void AssociateCircuitIds(string pdfText, ParsedInvoiceResult result)
    {
        if (result.Charges.Count == 0) return;

        var textLines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var circuitIdPattern = new Regex(@"Circuit\s*ID\s*:\s*([\w.]+)", RegexOptions.IgnoreCase);

        string? currentCircuitId = null;
        int nextChargeIndex = 0;
        ParsedCharge? previousMatchedCharge = null;

        foreach (var rawLine in textLines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var circuitMatch = circuitIdPattern.Match(line);
            if (circuitMatch.Success)
            {
                currentCircuitId = circuitMatch.Groups[1].Value.Trim();
                if (previousMatchedCharge != null && previousMatchedCharge.Line == null)
                    previousMatchedCharge.Line = currentCircuitId;
                continue;
            }

            if (nextChargeIndex < result.Charges.Count)
            {
                var charge = result.Charges[nextChargeIndex];
                if (charge.ChargeDescription != null &&
                    line.Contains(charge.ChargeDescription, StringComparison.OrdinalIgnoreCase))
                {
                    charge.Line ??= currentCircuitId;
                    previousMatchedCharge = charge;
                    nextChargeIndex++;
                }
            }
        }
    }

    /// <summary>
    /// Extracts only charge line items from the PDF text (no summary fields).
    /// Use as a fallback when ML/AI produces summary fields but no charges.
    /// </summary>
    /// <param name="chargeRules">
    /// All active <c>t_charge</c> rules for this carrier (anchors, section boundaries, skip, transforms).
    /// </param>
    public List<ParsedCharge> ExtractChargesFromText(string pdfText,
        IList<Entities.VendorParsingRule>? chargeRules = null)
    {
        var normalized = InvoiceTextNormalizer.Normalize(pdfText);
        var result = new ParsedInvoiceResult();
        ChargeExtractor.Extract(normalized, result.Charges, chargeRules);
        ChargeExtractor.DeduplicateCharges(result.Charges);
        AssociateCircuitIds(normalized, result);
        return result.Charges;
    }

    private static ParsedInvoiceResult SmartParse(string normalizedText,
        IList<Entities.VendorParsingRule>? chargeRules = null)
    {
        var result = new ParsedInvoiceResult();

        SummaryFieldsExtractor.Extract(normalizedText, result);

        if (AccountExtractor.TryExtract(normalizedText, out var account))
            result.SummaryFields["carrier_account"] = account;

        TotalExtractor.ExtractEndBal(normalizedText, result.SummaryFields);
        TotalExtractor.ComputeFallback(result.SummaryFields);   // derive from other fields if not found

        ChargeExtractor.Extract(normalizedText, result.Charges, chargeRules);

        return result;
    }

    public Invoice MapToInvoice(ParsedInvoiceResult parsed, int customerId, int carrierId,
        string? carrierName, string? carrierCode)
    {
        var invoice = new Invoice
        {
            CustomerId = customerId,
            CarrierId = carrierId,
            CarrierName = carrierName,
            CarrierCode = carrierCode,
        };

        SetField(invoice, parsed, "invoice_number", (inv, val) => inv.InvoiceNumber = val);
        SetField(invoice, parsed, "carrier_account", (inv, val) => inv.CarrierAccount = val);
        SetDateField(invoice, parsed, "invoice_date", (inv, val) => inv.InvoiceDate = val);
        SetDateField(invoice, parsed, "invoice_st_dtm", (inv, val) => inv.InvoiceStartDate = val);
        SetDateField(invoice, parsed, "invoice_end_dtm", (inv, val) => inv.InvoiceEndDate = val);
        SetDateField(invoice, parsed, "invoice_due_dtm", (inv, val) => inv.InvoiceDueDate = val);
        SetDecimalField(invoice, parsed, "beg_bal", (inv, val) => inv.BeginningBalance = val);
        SetDecimalField(invoice, parsed, "payment", (inv, val) => inv.Payment = val);
        SetDecimalField(invoice, parsed, "prev_adj", (inv, val) => inv.PreviousAdjustments = val);
        SetDecimalField(invoice, parsed, "curr_adj", (inv, val) => inv.CurrentAdjustments = val);
        SetDecimalField(invoice, parsed, "curr_chg", (inv, val) => inv.CurrentCharges = val);
        SetDecimalField(invoice, parsed, "curr_tax", (inv, val) => inv.CurrentTax = val);
        SetDecimalField(invoice, parsed, "end_bal", (inv, val) => inv.EndingBalance = val);

        return invoice;
    }

    private static void SetField(Invoice invoice, ParsedInvoiceResult parsed,
        string fieldName, Action<Invoice, string> setter)
    {
        if (parsed.SummaryFields.TryGetValue(fieldName, out var val) && val != null)
            setter(invoice, val);
    }

    private static void SetDateField(Invoice invoice, ParsedInvoiceResult parsed,
        string fieldName, Action<Invoice, DateTime> setter)
    {
        if (parsed.SummaryFields.TryGetValue(fieldName, out var val) && val != null)
        {
            if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                setter(invoice, dt);
        }
    }

    private static void SetDecimalField(Invoice invoice, ParsedInvoiceResult parsed,
        string fieldName, Action<Invoice, decimal> setter)
    {
        if (parsed.SummaryFields.TryGetValue(fieldName, out var val) && val != null
            && MonetaryParser.TryParse(val, out var d))
            setter(invoice, d);
    }
}
