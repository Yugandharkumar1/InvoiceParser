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
        ("TOTAL_DUE", new Regex($@"TOTAL\s+DUE\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AMOUNT_DUE", new Regex($@"AMOUNT\s+DUE\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("CURRENT_CHARGES", new Regex($@"CURRENT\s+CHARGES\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("REMAINING_BALANCE", new Regex($@"REMAINING\s+BALANCE\s*:?\s*({AmountCapture})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly Regex[] LegacyRegexes =
    {
        new(@"(?<!PREVIOUS\s+)BALANCE\s+DUE\s+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?<!Previous\s+)Balance\s+Due\s+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+(?:Amount\s+)?Due[ \t]+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Amount\s+Due[ \t]+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+due\s*:?[ \t]+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?:Please\s+)?Pay\s+(?:this\s+amount|Total)\s+\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"TOTAL\s+DUE\s*\r?\n\s*\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Total\s+Due:.*?\$?\s*(" + AmountCapture + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
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

    private static bool TryNormalizeAmount(string raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!MonetaryParser.TryParse(raw, out var d)) return false;
        normalized = d.ToString("F2", CultureInfo.InvariantCulture);
        return true;
    }
}
