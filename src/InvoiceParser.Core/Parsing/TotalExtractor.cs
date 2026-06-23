using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Services;

namespace InvoiceParser.Core.Parsing;

/// <summary>
/// Priority-based extraction for amount due (<c>end_bal</c>): TOTAL DUE, AMOUNT DUE, CURRENT CHARGES, REMAINING BALANCE, then legacy patterns.
/// Supports negative amounts and CR / parentheses credits via <see cref="MonetaryParser.TryParse"/>.
/// </summary>
public static class TotalExtractor
{
    private const string AmountCapture = @"-?\$?\s*\(?[\d,]+\.\d{2}\)?\s*(?:CR)?";

    private static readonly (string Name, Regex Regex)[] PriorityPatterns =
    {
        // Highest-confidence labels — appear directly on the invoice as the payable amount
        ("TOTAL_DUE",        new Regex($@"TOTAL\s+DUE\s*:?\s*({AmountCapture})",          RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AMOUNT_DUE",       new Regex($@"AMOUNT\s+DUE\s*:?\s*({AmountCapture})",         RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("NEW_BALANCE",      new Regex($@"NEW\s+BALANCE\s*:?\s*({AmountCapture})",         RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("BALANCE_DUE",      new Regex($@"(?<!PREVIOUS\s+)BALANCE\s+DUE\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("TOTAL_AMOUNT_DUE", new Regex($@"TOTAL\s+AMOUNT\s+DUE\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AMOUNT_OWED",      new Regex($@"AMOUNT\s+OWED\s*:?\s*({AmountCapture})",         RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("PAY_THIS_AMOUNT",  new Regex($@"PLEASE\s+PAY\s+THIS\s+AMOUNT\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("REMAINING_BALANCE",new Regex($@"REMAINING\s+BALANCE\s*:?\s*({AmountCapture})",   RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("CURRENT_CHARGES",  new Regex($@"CURRENT\s+CHARGES\s*:?\s*({AmountCapture})",     RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly Regex[] LegacyRegexes =
    {
        new(@"(?<!Previous\s+)Balance\s+Due\s+\$?\s*(" + AmountCapture + ")",           RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+(?:Amount\s+)?Due[ \t]+\$?\s*(" + AmountCapture + ")",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Amount\s+Due[ \t]+\$?\s*(" + AmountCapture + ")",                         RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+due\s*:?[ \t]+\$?\s*(" + AmountCapture + ")",                     RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:Please\s+)?Pay\s+(?:this\s+amount|Total)\s+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"TOTAL\s+DUE\s*\r?\n\s*\$?\s*(" + AmountCapture + ")",                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+Due:.*?\$?\s*(" + AmountCapture + ")",                            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"New\s+Balance\s*\r?\n\s*\$?\s*(" + AmountCapture + ")",                  RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Balance\s+Due\s*\r?\n\s*\$?\s*(" + AmountCapture + ")",                  RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Amount\s+Due\s*\r?\n\s*\$?\s*(" + AmountCapture + ")",                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+Due\s+(?:by|on)\s+[\w/,\s]+\s+\$?\s*(" + AmountCapture + ")",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public static void ExtractEndBal(string text, IDictionary<string, string?> summaryFields)
    {
        foreach (var (_, rx) in PriorityPatterns)
        {
            var m = rx.Match(text);
            if (!m.Success) continue;
            var raw = m.Groups[1].Value.Trim();
            if (TryNormalizeAmount(raw, out var normalized))
            {
                summaryFields["end_bal"] = normalized;
                return;
            }
        }

        foreach (var rx in LegacyRegexes)
        {
            var m = rx.Match(text);
            if (!m.Success || m.Groups.Count < 2) continue;
            var raw = m.Groups[1].Value.Trim();
            if (TryNormalizeAmount(raw, out var normalized))
            {
                summaryFields["end_bal"] = normalized;
                return;
            }
        }
    }

    /// <summary>
    /// If <c>end_bal</c> was not found or is zero, computes it from the other summary fields:
    /// <para>Balance Due = Previous Balance − Payments + Previous Adjustments + Current Adjustments + Current Charges + Taxes</para>
    /// Only sets the value when at least one of <c>curr_chg</c> or <c>curr_tax</c> is non-zero,
    /// to avoid writing a meaningless 0.00 when nothing was parsed.
    /// </summary>
    public static void ComputeFallback(IDictionary<string, string?> summaryFields)
    {
        if (summaryFields.TryGetValue("end_bal", out var existing)
            && !string.IsNullOrWhiteSpace(existing)
            && decimal.TryParse(existing, NumberStyles.Any, CultureInfo.InvariantCulture, out var ex)
            && ex != 0m)
        {
            return; // Already extracted a non-zero value — leave it
        }

        static decimal Get(IDictionary<string, string?> fields, string key)
        {
            if (!fields.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return 0m;
            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        var begBal   = Get(summaryFields, "beg_bal");
        var payment  = Get(summaryFields, "payment");
        var prevAdj  = Get(summaryFields, "prev_adj");
        var currAdj  = Get(summaryFields, "curr_adj");
        var currChg  = Get(summaryFields, "curr_chg");
        var currTax  = Get(summaryFields, "curr_tax");

        // Need at least curr_chg or curr_tax to produce a meaningful result
        if (currChg == 0m && currTax == 0m) return;

        var computed = begBal - payment + prevAdj + currAdj + currChg + currTax;
        summaryFields["end_bal"] = computed.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static bool TryNormalizeAmount(string raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!MonetaryParser.TryParse(raw, out var d)) return false;
        normalized = d.ToString("F2", CultureInfo.InvariantCulture);
        return true;
    }
}
