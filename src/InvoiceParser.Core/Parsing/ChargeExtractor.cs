using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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

    public static void Extract(string pdfText, IList<ParsedCharge> charges)
    {
        var lines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        string? currentSection = null;
        string? currentLine = null;

        foreach (var rawLine in lines)
        {
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

            var isSectionHeader = false;
            foreach (var header in SectionHeaders)
            {
                if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                {
                    isSectionHeader = true;
                    if (!ChargeLine.IsMatch(line))
                        currentSection = header;
                    break;
                }
            }

            if (currentSection == null) continue;

            if (isSectionHeader) continue;
            if (SkipLinePrefixes.Any(sp => line.StartsWith(sp, StringComparison.OrdinalIgnoreCase))) continue;

            if (SummaryTotalLine.IsMatch(line)) continue;

            var match = ChargeLine.Match(line);
            if (!match.Success) continue;

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

            var charge = new ParsedCharge
            {
                ChargeDescription = desc,
                Line = currentLine,
            };

            var amountToken = match.Groups[2].Value.Trim();
            var explicitCr = match.Groups[3].Success;
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

        DeduplicateCharges(charges);
    }

    private static bool IsNoiseDescription(string desc)
    {
        var d = desc.Trim();
        if (d.Length <= 12 && Regex.IsMatch(d, @"^(Subtotal|Total)\b", RegexOptions.IgnoreCase))
            return true;
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
