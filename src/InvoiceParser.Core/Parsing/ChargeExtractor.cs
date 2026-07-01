using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services;

namespace InvoiceParser.Core.Parsing;

/// <summary>
/// Line-item charge extraction from invoice text (section-based), with credits and deduplication.
/// </summary>
public static class ChargeExtractor
{
    private static readonly string[] SectionHeaders =
    {
        "Recurring Charges", "One Time Charges", "Prorated Charges", "Usage Charges",
        "BASIC TELEPHONE", "NON-BASIC TELEPHONE", "OTHER",
        "Monthly Service", "Equipment Charges", "Internet Service",
        "Voice Service", "Data Service",
        "MONTHLY USAGE FOR TELEPHONE",
        "Product/Service",
    };

    private static readonly string[] SkipLinePrefixes =
    {
        "Previous Statement", "Current Charges Subtotal", "Current Charges $",
        "BALANCE DUE", "Balance Due", "Payments", "Subtotal",
        "Page ", "Page:", "Invoice Number", "Account Number", "Invoice Date",
        "Due Date", "Security Code", "Late Fee",
        "Summary Account", "Pay Online", "Make payments",
        "Checks", "Charter Communications", "Contact Us",
        "details on following", "AMOUNT PAID",
        "PREVIOUS BALANCE", "CURRENT CHARGES",
        "ACCOUNT NO", "TELEPHONE NO", "BILL DATE",
        "Surcharges and Other Charges", "Surcharges and Other",
        "Taxes, Governmental Surcharges",
        "Monthly Charges", "Usage and Purchase Charges",
        "Total Current Charges", "Total Voice", "Total Data", "Total Messaging",
        "Description Date",
        "CURRENT BILLING AMOUNT", "SUB-TOTAL",
        "PAYMENT(S)", "BALANCE FROM",
        "SUMMARY BY SERVICE", "Total Due",
        "MESSAGE CENTER", "Frequently Asked",
        "The carrier you have chosen",
    };

    /// <summary>
    /// Description, amount (supports -, parens, CR), end of line.
    /// </summary>
    private static readonly Regex ChargeLine = new(
        @"^(.+?)\s+\$?\s*(-?\(?[\d,]*\.\d{2}\)?)\s*(CR)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnitPricingPattern = new(@"\s+\d+\s*@\s*[\d,.]+\s*$", RegexOptions.Compiled);
    private static readonly Regex PhoneLinePattern = new(@"\(\d{3}\)\d{3}-\d{4}", RegexOptions.Compiled);
    private static readonly Regex PhoneTotalPattern = new(@"^\(\d{3}\)\d{3}-\d{4}\s+TOTAL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SummaryTotalLine = new(
        @"^\s*(Subtotal|Total)\s*:?\s*\$?\s*-?[\d,]*\.\d{2}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Non-charge lines (payments, thank-you, adjustments) must not become line items.</summary>
    private static readonly string[] ExcludedChargeDescriptionSubstrings =
    {
        "PAYMENT", "THANK YOU", "ADJUSTMENT",
    };

    /// <summary>
    /// Regex that finds the last decimal number on a line (used as fallback amount for table_row rules).
    /// </summary>
    private static readonly Regex LastDecimalOnLine = new(
        @"\$?\s*([\d,]+\.\d{2})\s*$", RegexOptions.Compiled);

    /// <param name="chargeRules">
    /// All active <c>t_charge</c> rules for this carrier.  The extractor categorises them
    /// internally: <c>line_anchor</c>, <c>location_anchor</c>, <c>section_start</c>,
    /// <c>section_end</c>, <c>skip</c>, and <c>table_row</c>.
    /// </param>
    public static void Extract(string pdfText, IList<ParsedCharge> charges,
        IList<VendorParsingRule>? chargeRules = null)
    {
        var lines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // ── Categorise rules once ─────────────────────────────────────────────
        var lineAnchorRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" &&
                        (r.FieldType == "line_anchor" || r.FieldName == "line_number"))
            .ToList();
        var locationAnchorRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" &&
                        (r.FieldType == "location_anchor" || r.FieldName == "location"))
            .ToList();
        var sectionStartRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" && r.FieldType == "section_start")
            .ToList();
        var sectionEndRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" && r.FieldType == "section_end")
            .ToList();
        var lineSkipRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" && r.FieldType == "skip")
            .ToList();
        // Table-row rules: trigger line contains the amount; description comes from an offset line above/below.
        var tableRowRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" && r.FieldType == "table_row")
            .ToList();
        // Line-before-price rules: trigger line matched; description auto-found by scanning upward.
        var lineBeforePriceRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" && r.FieldType == "line_before_price")
            .ToList();
        // Charge-description cleanup rules: applied as a post-processing pass to EVERY captured
        // description that matches the condition, regardless of how it was extracted.
        // Use these to strip dates, account numbers, or any repeating noise from descriptions.
        var chargeDescCleanupRules = chargeRules?
            .Where(r => r.IsActive && r.TargetTable == "t_charge" &&
                        r.FieldName == "charge_desc" &&
                        !string.IsNullOrWhiteSpace(r.TransformationsJson))
            .ToList();

        bool hasAnchorRules          = (lineAnchorRules?.Count > 0) || (locationAnchorRules?.Count > 0);
        bool hasTableRowRules        = tableRowRules?.Count > 0;
        bool hasLineBeforePriceRules = lineBeforePriceRules?.Count > 0;
        bool hasDescCleanupRules     = chargeDescCleanupRules?.Count > 0;

        string? currentSection  = null;
        string? currentLine     = null;
        string? currentLocation = null;

        // ── TEMP DEBUG: write PDF lines visible to extractor ─────────────────
        var _debugLines = new System.Text.StringBuilder();
        _debugLines.AppendLine($"=== ChargeExtractor: {lines.Length} raw lines ===");
        for (int di = 0; di < lines.Length; di++)
            _debugLines.AppendLine($"[{di:D3}] {lines[di]}");
        try { System.IO.File.WriteAllText(@"C:\Temp\charge_debug.txt", _debugLines.ToString()); } catch { }
        // ─────────────────────────────────────────────────────────────────────

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var rawLine = lines[lineIdx];
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 4) continue;

            if (Regex.IsMatch(line, @"^Circuit\s*ID\s*:", RegexOptions.IgnoreCase)) continue;

            if (PhoneTotalPattern.IsMatch(line))
            {
                currentLine = null;
                continue;
            }

            if (line.StartsWith("SUMMARY FOR", StringComparison.OrdinalIgnoreCase))
            {
                var phoneMatch = PhoneLinePattern.Match(line);
                if (phoneMatch.Success)
                    currentLine = phoneMatch.Value;
                currentSection ??= "MONTHLY USAGE FOR TELEPHONE";
                continue;
            }

            if (line.StartsWith("MONTHLY USAGE FOR TELEPHONE", StringComparison.OrdinalIgnoreCase))
            {
                if (currentLine == null)
                {
                    var phoneMatch = PhoneLinePattern.Match(line);
                    if (phoneMatch.Success)
                        currentLine = phoneMatch.Value;
                }
                currentSection ??= "MONTHLY USAGE FOR TELEPHONE";
                continue;
            }

            if (Regex.IsMatch(line, @"^Taxes,?\s*Fees", RegexOptions.IgnoreCase))
            {
                currentSection = "Taxes, Fees & Surcharges";
                var taxHeaderMatch = ChargeLine.Match(line);
                if (taxHeaderMatch.Success) continue;
            }

            // ── Configurable section-end rules ───────────────────────────────
            // Evaluated BEFORE anchor rules so a line like "Total Services Detail for AAA..."
            // can both close the section AND set currentLocation without skipping the boundary check.
            if (currentSection != null && sectionEndRules?.Count > 0)
            {
                if (sectionEndRules.Any(r =>
                        ConditionEvaluator.Evaluate(line, r.ConditionType, r.RegexPattern)))
                {
                    currentSection = null;
                    // Fall through — still allow anchor extraction from this line.
                }
            }

            // ── Configurable section-start rules ─────────────────────────────
            // Evaluated BEFORE anchor rules so a line like "MONTHLY CHARGES FOR 000-401-6568"
            // can both open the section AND set currentLine without skipping the boundary check.
            if (sectionStartRules?.Count > 0)
            {
                if (sectionStartRules.Any(r =>
                        ConditionEvaluator.Evaluate(line, r.ConditionType, r.RegexPattern)))
                {
                    currentSection = "configured";
                    // Fall through — still allow anchor extraction from this line.
                }
            }

            // ── Configurable anchor rules ─────────────────────────────────────
            // Runs after section boundary checks so the same line can set the section
            // boundary AND update currentLine / currentLocation in one pass.
            if (hasAnchorRules)
            {
                bool anchorMatched = false;
                if (TryApplyAnchorRule(line, lineAnchorRules, out var extractedLine, pdfText))
                {
                    currentLine = extractedLine;
                    anchorMatched = true;
                }
                if (TryApplyAnchorRule(line, locationAnchorRules, out var extractedLocation, pdfText))
                {
                    currentLocation = extractedLocation;
                    anchorMatched = true;
                }
                // Skip charge extraction for this line — it is a header/anchor line, not a charge.
                if (anchorMatched) continue;
            }
            else if (currentSection == "configured" &&
                     sectionStartRules?.Any(r =>
                         ConditionEvaluator.Evaluate(line, r.ConditionType, r.RegexPattern)) == true)
            {
                // No anchor rules — section-start line is just a boundary marker, not a charge.
                continue;
            }

            // ── Hard-coded section headers (backward-compatible fallback) ──────
            var isSectionHeader = false;
            foreach (var header in SectionHeaders)
            {
                if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                {
                    isSectionHeader = true;
                    currentSection = header; // Always open the section; the header line is excluded from charge extraction below
                    break;
                }
            }

            if (currentSection == null) continue;
            if (isSectionHeader) continue;
            if (SkipLinePrefixes.Any(sp => line.StartsWith(sp, StringComparison.OrdinalIgnoreCase))) continue;
            if (SummaryTotalLine.IsMatch(line)) continue;

            // ── Table-row rules (multi-line product/amount tables) ────────────
            // These take priority over standard ChargeLine matching so the trigger
            // line is not also processed as a regular charge.
            if (hasTableRowRules)
            {
                var tableMatch = tableRowRules!.FirstOrDefault(r =>
                    ConditionEvaluator.Evaluate(line, r.ConditionType, r.RegexPattern));

                if (tableMatch != null)
                {
                    // Resolve description line (offset from trigger line index).
                    var descIdx = lineIdx + tableMatch.DescriptionLineOffset;
                    if (descIdx >= 0 && descIdx < lines.Length)
                    {
                        var descLine = lines[descIdx].Trim();
                        if (!string.IsNullOrWhiteSpace(descLine) && descLine.Length >= 2)
                        {
                            // Apply optional transforms to the description
                            var steps = TransformPipeline.Deserialize(tableMatch.TransformationsJson);
                            if (steps != null)
                                descLine = TransformPipeline.Apply(descLine, steps, pdfText, matchedLine: descLine);

                            // Skip if matches a skip rule
                            if (lineSkipRules?.Count > 0 &&
                                lineSkipRules.Any(r =>
                                    ConditionEvaluator.Evaluate(descLine, r.ConditionType, r.RegexPattern)))
                            {
                                continue;
                            }

                            // Extract amount from trigger line
                            decimal? amount = null;
                            if (!string.IsNullOrWhiteSpace(tableMatch.AmountPattern))
                            {
                                var amtMatch = Regex.Match(line, tableMatch.AmountPattern);
                                if (amtMatch.Success && MonetaryParser.TryParse(
                                        amtMatch.Groups.Count > 1 ? amtMatch.Groups[1].Value : amtMatch.Value,
                                        out var parsedAmt))
                                    amount = parsedAmt;
                            }
                            else
                            {
                                // Fallback: last decimal number on the trigger line
                                var lastAmt = LastDecimalOnLine.Match(line);
                                if (lastAmt.Success && MonetaryParser.TryParse(lastAmt.Groups[1].Value, out var parsedAmt))
                                    amount = parsedAmt;
                            }

                            charges.Add(new ParsedCharge
                            {
                                ChargeDescription = descLine,
                                Amount            = amount,
                                Line              = currentLine,
                                Location          = currentLocation,
                            });
                        }
                    }
                    continue; // do not process trigger line as a normal charge
                }
            }

            // ── Line-before-price rules ───────────────────────────────────────
            // When a trigger line is matched (e.g. contains "RECURRING CHARGE"),
            // scan backwards to find the nearest non-empty, non-trigger line and
            // use it as the charge description. No fixed offset required.
            if (hasLineBeforePriceRules)
            {
                var lbpMatch = lineBeforePriceRules!.FirstOrDefault(r =>
                    ConditionEvaluator.Evaluate(line, r.ConditionType, r.RegexPattern));

                if (lbpMatch != null)
                {
                    string? descLine = null;

                    for (int scanIdx = lineIdx - 1; scanIdx >= 0 && descLine == null; scanIdx--)
                    {
                        var candidate = lines[scanIdx].Trim();
                        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 2) continue;

                        // Stop at hard section boundaries
                        if (SectionHeaders.Any(h => candidate.StartsWith(h, StringComparison.OrdinalIgnoreCase))) break;
                        if (sectionStartRules?.Any(r => ConditionEvaluator.Evaluate(candidate, r.ConditionType, r.RegexPattern)) == true) break;

                        // Skip other trigger lines (other RECURRING CHARGE rows)
                        if (lineBeforePriceRules!.Any(r => ConditionEvaluator.Evaluate(candidate, r.ConditionType, r.RegexPattern))) continue;

                        // Skip table_row trigger lines too
                        if (hasTableRowRules && tableRowRules!.Any(r => ConditionEvaluator.Evaluate(candidate, r.ConditionType, r.RegexPattern))) continue;

                        // Skip user-defined skip lines
                        if (lineSkipRules?.Any(r => ConditionEvaluator.Evaluate(candidate, r.ConditionType, r.RegexPattern)) == true) continue;

                        // Skip pure summary/total lines
                        if (SummaryTotalLine.IsMatch(candidate)) continue;
                        if (SkipLinePrefixes.Any(sp => candidate.StartsWith(sp, StringComparison.OrdinalIgnoreCase))) continue;

                        descLine = candidate;
                    }

                    if (!string.IsNullOrWhiteSpace(descLine))
                    {
                        var steps = TransformPipeline.Deserialize(lbpMatch.TransformationsJson);
                        if (steps != null)
                            descLine = TransformPipeline.Apply(descLine, steps, pdfText, matchedLine: descLine);

                        var lastAmt = LastDecimalOnLine.Match(line);
                        decimal? amount = null;
                        if (lastAmt.Success && MonetaryParser.TryParse(lastAmt.Groups[1].Value, out var parsedAmt))
                            amount = parsedAmt;

                        charges.Add(new ParsedCharge
                        {
                            ChargeDescription = descLine,
                            Amount            = amount,
                            Line              = currentLine,
                            Location          = currentLocation,
                        });
                    }

                    continue; // do not process trigger line as a normal charge
                }
            }

            // ── Standard single-line charge extraction ────────────────────────
            var match = ChargeLine.Match(line);
            if (!match.Success)
            {
                // Word-wrapped charges: description on one or more lines, amount on a separate line beneath.
                match = TryForwardJoin(lines, lineIdx, line, out var forwardConsumed);
                if (!match.Success) continue;
                lineIdx += forwardConsumed;
            }

            var desc = match.Groups[1].Value.Trim();

            if (string.IsNullOrWhiteSpace(desc) || desc.Length < 3) continue;
            if (ExcludedChargeDescriptionSubstrings.Any(s =>
                    desc.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (ExcludedChargeDescriptionSubstrings.Any(s =>
                    line.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (SkipLinePrefixes.Any(sp => desc.StartsWith(sp, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsNoiseDescription(desc)) continue;
            if (Regex.IsMatch(desc, @"^\d{2}/\d{2}/\d{4}$")) continue;
            if (Regex.IsMatch(desc, @"^\(\d{3}\)\d{3}-\d{4}")) continue;

            desc = UnitPricingPattern.Replace(desc, "").Trim();
            if (string.IsNullOrWhiteSpace(desc) || desc.Length < 3) continue;

            if (lineSkipRules?.Count > 0 &&
                lineSkipRules.Any(r =>
                    ConditionEvaluator.Evaluate(desc, r.ConditionType, r.RegexPattern)))
                continue;

            var charge = new ParsedCharge
            {
                ChargeDescription = desc,
                Line              = currentLine,
                Location          = currentLocation,
            };

            var amountToken = match.Groups[2].Value.Trim();
            var explicitCr  = match.Groups[3].Success;
            var parenCredit = amountToken.TrimStart().StartsWith("(");
            if (MonetaryParser.TryParse(amountToken, out var amt))
            {
                if (explicitCr && amt > 0)
                    charge.Amount = -amt;
                else if (parenCredit && amt > 0)
                    charge.Amount = -amt;
                else
                    charge.Amount = amt;
            }

            charges.Add(charge);
        }

        // ── TEMP DEBUG: append captured charges ────────────────────────────────
        _debugLines.AppendLine("=== Captured charges (before dedup/cleanup) ===");
        foreach (var c in charges)
            _debugLines.AppendLine($"  desc={c.ChargeDescription}  amt={c.Amount}");
        try { System.IO.File.WriteAllText(@"C:\Temp\charge_debug.txt", _debugLines.ToString()); } catch { }
        // ─────────────────────────────────────────────────────────────────────

        // ── Post-processing: apply charge_desc cleanup rules to every description ──
        // These rules run after all charges are collected so they cover descriptions
        // produced by standard extraction, table_row, AND line_before_price paths.
        if (hasDescCleanupRules && charges.Count > 0)
        {
            foreach (var charge in charges)
            {
                if (string.IsNullOrWhiteSpace(charge.ChargeDescription)) continue;

                foreach (var rule in chargeDescCleanupRules!)
                {
                    // Condition check: if a pattern is set, only apply to matching descriptions.
                    // If no pattern is set (or condition is "always"), apply to every description.
                    bool conditionMet = string.IsNullOrWhiteSpace(rule.RegexPattern)
                        || ConditionEvaluator.Evaluate(charge.ChargeDescription, rule.ConditionType, rule.RegexPattern);

                    if (!conditionMet) continue;

                    var steps = TransformPipeline.Deserialize(rule.TransformationsJson);
                    if (steps == null) continue;

                    var cleaned = TransformPipeline.Apply(charge.ChargeDescription, steps, pdfText,
                        matchedLine: charge.ChargeDescription);

                    if (!string.IsNullOrWhiteSpace(cleaned))
                        charge.ChargeDescription = cleaned;
                }
            }
        }

        DeduplicateCharges(charges);
    }

    /// <summary>
    /// Attempts to form a valid charge line by joining <paramref name="currentLine"/> with up to
    /// <c>maxLookahead</c> subsequent non-empty lines. Handles word-wrapped PDF layouts where a
    /// charge description overflows onto the next line and/or the amount appears on its own line.
    /// Returns a successful <see cref="Match"/> and the number of additional lines consumed,
    /// or <see cref="Match.Empty"/> if no valid charge can be assembled within the lookahead window.
    /// </summary>
    private static Match TryForwardJoin(string[] lines, int startIdx, string currentLine,
        out int linesConsumed, int maxLookahead = 3)
    {
        linesConsumed = 0;
        var accumulated = currentLine.TrimEnd();

        for (int i = startIdx + 1; i < lines.Length && (i - startIdx) <= maxLookahead; i++)
        {
            var next = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(next)) continue;

            // Do not cross section boundaries or skip-list lines
            if (SectionHeaders.Any(h => next.StartsWith(h, StringComparison.OrdinalIgnoreCase))) break;
            if (SkipLinePrefixes.Any(sp => next.StartsWith(sp, StringComparison.OrdinalIgnoreCase))) break;

            accumulated += " " + next;
            var m = ChargeLine.Match(accumulated);
            if (m.Success)
            {
                linesConsumed = i - startIdx;
                return m;
            }
        }

        return Match.Empty;
    }

    private static bool IsNoiseDescription(string desc)
    {
        var d = desc.Trim();
        if (d.Length <= 12 && Regex.IsMatch(d, @"^(Subtotal|Total)\b", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Tests <paramref name="line"/> against each anchor rule in <paramref name="rules"/>.
    /// When a match is found, applies transforms and returns the extracted context value.
    /// <paramref name="pdfText"/> is forwarded to support join_next_line / join_prev_line transforms.
    /// </summary>
    private static bool TryApplyAnchorRule(string line,
        IList<VendorParsingRule>? rules, out string? extracted, string? pdfText = null)
    {
        extracted = null;
        if (rules == null || rules.Count == 0) return false;

        foreach (var rule in rules)
        {
            if (!ConditionEvaluator.Evaluate(line, rule.ConditionType, rule.RegexPattern))
                continue;

            var value = line;
            var steps = TransformPipeline.Deserialize(rule.TransformationsJson);
            if (steps != null)
                value = TransformPipeline.Apply(value, steps, pdfText, matchedLine: line);

            extracted = string.IsNullOrWhiteSpace(value) ? null : value;
            return true;
        }

        return false;
    }

    /// <summary>Remove duplicate rows (same description, amount, line after normalize).</summary>
    public static void DeduplicateCharges(IList<ParsedCharge> charges)
    {
        if (charges.Count <= 1) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new List<ParsedCharge>(charges.Count);

        foreach (var c in charges)
        {
            var desc = (c.ChargeDescription ?? "").Trim().ToLowerInvariant();
            var amt = c.Amount?.ToString("F2", CultureInfo.InvariantCulture) ?? "";
            var line = (c.Line ?? "").Trim().ToLowerInvariant();
            var key = desc + "|" + amt + "|" + line;
            if (seen.Add(key))
                keep.Add(c);
        }

        if (keep.Count == charges.Count) return;

        charges.Clear();
        foreach (var c in keep)
            charges.Add(c);
    }
}
