using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Services;

public class VerizonWirelessParser
{
    private enum InvoiceSection
    {
        None,
        MonthlyCharges,
        FeaturesAndAddOns,
        Surcharges,
        OtherChargesAndCredits,
        TaxesAndFees,
        UsageAndPurchase,
        Equipment,
    }

    #region Compiled Regex Patterns

    private static readonly Regex PhonePattern = new(@"\b(\d{3}-\d{3}-\d{4})\b", RegexOptions.Compiled);

    private static readonly Regex VoiceUsagePattern = new(
        @"(?:Calling\s+Plan|Previous\s+Calling\s+Plan|New\s+Calling\s+Plan)" +
        @"(?:\s*\(\d{2}/\d{2}\s*-\s*\d{2}/\d{2}\))?\s+minutes\s+(unlimited|\d[\d,]*)\s+([\d,]+)\s+([\-\$\d,.]+)\s+([\-\$\d,.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DataUsagePattern = new(
        @"((?:Gigabyte|Megabyte|Kilobyte|5G\s+Ultra\s+Wideband)\s*(?:Usage)?" +
        @"(?:\s*\(\d{2}/\d{2}\s*-\s*\d{2}/\d{2}\))?)\s+" +
        @"(gigabytes|megabytes|kilobytes)\s+(unlimited|[\d,.]+)\s+([\d,.]+)\s+([\-\$\d,.]+)\s+([\-\$\d,.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TextUsagePattern = new(
        @"(Text|Messaging\s+Plan)(?:\s*\(\d{2}/\d{2}\s*-\s*\d{2}/\d{2}\))?\s+" +
        @"messages\s+(unlimited|\d[\d,]*)\s+([\d,]+)\s+([\-\$\d,.]+)\s+([\-\$\d,.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalVoiceCostPattern = new(
        @"Total\s+Voice\s+\$?([\-\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalDataCostPattern = new(
        @"Total\s+Data\s+\$?([\-\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalMessagingCostPattern = new(
        @"Total\s+Messaging\s+\$?([\-\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthlyChargesPattern = new(
        @"Monthly\s+Charges(?:\s+\$?-?[\d,.]+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlanLinePattern = new(
        @"^(.+?)\s+\d{2}/\d{2}\s*-\s*\d{2}/\d{2}\s+(-?)\.?\$([\d,.]+)(?:\s.*)?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex BlockHeaderPattern = new(
        @"Charges\s+by\s+line\s+details(?:\s*\(continued\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ChargeLineItemPattern = new(
        @"^(.+?)\s+(-?)\.?\$(\d[\d,]*\.\d{2})(?:\s.*)?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex StandaloneAmountPattern = new(
        @"^(-?)\.?\$(\d[\d,]*\.\d{2})\s*$",
        RegexOptions.Compiled);

    private static readonly Regex DollarOffPattern = new(
        @"^\$\d+\s+Off\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BillingPeriodPattern = new(
        @"Billing\s+period:\s+([A-Za-z]+\s+\d{1,2})\s*-\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AccountLevelStartPattern = new(
        @"Account\s+Level\s+Charges?\s+Details?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OverviewTableStartPattern = new(
        @"Account\s+Charges\s+and\s+Line\s+Charges",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DateRangeInlinePattern = new(
        @"\s*\(.*?\)", RegexOptions.Compiled);

    private static readonly Regex UrlPattern = new(
        @"(?:\.com|\.net|\.org|\.gov|www\.|https?://)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InformationalTextPattern = new(
        @"^(?:See\s|Visit\s|Go\s+to\s|Learn\s+more|For\s+(?:more|details|info)|Contact\s|Call\s+\d|Terms\s+and|©|\*{1,3}\s|Page\s+\d|continued\s+on)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    #endregion

    #region Section Detection & Skip Lists

    private static readonly HashSet<string> ChargeSkipPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Total Current Charges", "Total Voice", "Total Data", "Total Messaging",
        "Total Monthly Charges", "Total Monthly", "Total Surcharges",
        "Total Equipment Charges", "Total Equipment",
        "Total Other Charges", "Total Other",
        "Total Taxes",
        "Monthly Charges", "Usage and Purchase Charges",
        "Surcharges and Other Charges", "Surcharges and Other",
        "Surcharges", "Other Charges and Credits",
        "Taxes, Governmental Surcharges", "Taxes, Governmental",
        "Charges by line details",
        "Total Payments", "Balance Forward", "Balance from last", "Balance",
        "Total due", "Total Charges", "Sub Total",
        "Your Plan", "Your February", "Your March", "Your January",
        "Your April", "Your May", "Your June", "Your July",
        "Your August", "Your September", "Your October", "Your November", "Your December",
        "Bill summary",
        "Includes proration", "Detail Billing",
        "Equipment Charges",
        "Features & AddOns", "Features and AddOns", "Features and Add Ons",
        "Account Level Charges",
        "Paid", "Past Due", "buyout",
    };

    private static readonly HashSet<string> ChargeSkipExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "Plan", "Services", "Charges", "Credits", "Discounts",
    };

    private static readonly HashSet<string> SectionHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Monthly Charges",
        "Features & AddOns",
        "Features and AddOns",
        "Features and Add Ons",
        "Surcharges and Other Charges",
        "Surcharges",
        "Other Charges and Credits",
        "Taxes, Governmental Surcharges and Fees",
        "Taxes, Governmental Surcharges & Fees",
        "Usage and Purchase Charges",
        "Equipment Charges",
    };

    #endregion

    #region Public API

    public bool IsVerizonWirelessFormat(string pdfText)
    {
        return pdfText.Contains("Charges by line details", StringComparison.OrdinalIgnoreCase)
            && (pdfText.Contains("Usage and Purchase Charges", StringComparison.OrdinalIgnoreCase)
                || pdfText.Contains("Monthly Charges", StringComparison.OrdinalIgnoreCase));
    }

    public void Parse(string pdfText, ParsedInvoiceResult result)
    {
        var lineBlocks = SplitIntoLineBlocks(pdfText);

        result.Charges.Clear();

        var inventoryCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chargeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in lineBlocks)
        {
            var phoneNumber = block.PhoneNumber;
            if (string.IsNullOrEmpty(phoneNumber)) continue;

            ExtractUsageItems(block, result.Usages, usageKeys);
            ExtractChargeItems(block, result.Charges, chargeKeys);

            if (!inventoryCreated.Contains(phoneNumber))
            {
                var inventory = ExtractInventory(block);
                if (inventory != null)
                {
                    result.Inventories.Add(inventory);
                    inventoryCreated.Add(phoneNumber);
                }
            }
        }

        ExtractAccountLevelCharges(pdfText, result.Charges, chargeKeys);
        ExtractOverviewSurcharges(pdfText, result);
        ExtractVerizonSummaryFields(pdfText, result);
    }

    #endregion

    #region Helpers (adapted from reference project's ParseAmount / isDecimal / RemoveDateRange)

    /// <summary>
    /// Robust amount parsing: handles $, commas, CR (credit), and parenthesized negatives.
    /// Adapted from the reference project's ParseAmount pattern.
    /// </summary>
    internal static decimal ParseAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;

        string cleaned = raw.Replace("$", "").Replace(",", "").Trim();
        bool isNegative = false;

        if (cleaned.IndexOf("cr", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            isNegative = true;
            cleaned = cleaned.Replace("CR", "").Replace("cr", "").Replace("-", "").Trim();
        }
        else if (cleaned.Contains('(') && cleaned.Contains(')'))
        {
            isNegative = true;
            cleaned = cleaned.Replace("(", "").Replace(")", "").Replace("-", "").Trim();
        }
        else if (cleaned.StartsWith('-'))
        {
            isNegative = true;
            cleaned = cleaned.TrimStart('-').Trim();
        }

        if (cleaned.StartsWith('.'))
            cleaned = "0" + cleaned;

        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return isNegative ? -result : result;

        return 0m;
    }

    /// <summary>
    /// Removes inline date-range parenthetical text, e.g. "(01/24 - 02/23)" from a USOC name.
    /// From reference project's RemoveDateRange pattern.
    /// </summary>
    private static string RemoveDateRange(string input)
        => DateRangeInlinePattern.Replace(input, "").Trim();

    private static bool IsInformationalText(string text)
        => UrlPattern.IsMatch(text) || InformationalTextPattern.IsMatch(text);

    private static string BuildChargeKey(string phone, string desc, decimal amount)
        => $"{phone}|{desc.ToUpperInvariant()}|{amount:F2}";

    private static string BuildUsageKey(string phone, string usocName, string usageType)
        => $"{phone}|{(usocName ?? "").ToUpperInvariant()}|{usageType}";

    private static string CleanCost(string value)
    {
        string cleaned = value.Replace("$", "").Replace(",", "").Replace("--", "0.00").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return "0.00";
        return cleaned;
    }

    private static string NormalizeLimit(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("unlimited", StringComparison.OrdinalIgnoreCase) ||
            value == "--")
            return "-1";

        return value.Replace(",", "");
    }

    public static decimal ConvertToGb(decimal value, string unit)
    {
        return unit switch
        {
            "kilobytes" => value / 1024m / 1024m,
            "megabytes" => value / 1024m,
            _ => value,
        };
    }

    private static decimal ParseDecimalSafe(string value)
    {
        string cleaned = value.Replace(",", "").Replace("$", "").Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        return 0;
    }

    /// <summary>
    /// Determines the current invoice section from a text line.
    /// Mirrors the reference project's section flag toggles.
    /// </summary>
    private static InvoiceSection DetectSection(string normalizedLine)
    {
        if (normalizedLine.StartsWith("monthly charges", StringComparison.OrdinalIgnoreCase))
            return InvoiceSection.MonthlyCharges;
        if (normalizedLine.StartsWith("features", StringComparison.OrdinalIgnoreCase) &&
            (normalizedLine.Contains("addon", StringComparison.OrdinalIgnoreCase) ||
             normalizedLine.Contains("add-on", StringComparison.OrdinalIgnoreCase) ||
             normalizedLine.Contains("add on", StringComparison.OrdinalIgnoreCase)))
            return InvoiceSection.FeaturesAndAddOns;
        if (normalizedLine.StartsWith("surcharges and other", StringComparison.OrdinalIgnoreCase) ||
            normalizedLine.Equals("surcharges", StringComparison.OrdinalIgnoreCase))
            return InvoiceSection.Surcharges;
        if (normalizedLine.StartsWith("other charges and credits", StringComparison.OrdinalIgnoreCase))
            return InvoiceSection.OtherChargesAndCredits;
        if (normalizedLine.StartsWith("taxes, governmental", StringComparison.OrdinalIgnoreCase))
            return InvoiceSection.TaxesAndFees;
        if (normalizedLine.StartsWith("usage and purchase", StringComparison.OrdinalIgnoreCase))
            return InvoiceSection.UsageAndPurchase;
        if (normalizedLine.StartsWith("equipment charges", StringComparison.OrdinalIgnoreCase))
            return InvoiceSection.Equipment;

        return InvoiceSection.None;
    }

    #endregion

    #region Block Splitting

    private List<LineBlock> SplitIntoLineBlocks(string pdfText)
    {
        var blocks = new List<LineBlock>();
        var headerMatches = BlockHeaderPattern.Matches(pdfText);

        if (headerMatches.Count == 0) return blocks;

        for (int i = 0; i < headerMatches.Count; i++)
        {
            int start = headerMatches[i].Index + headerMatches[i].Length;
            int end = (i + 1 < headerMatches.Count) ? headerMatches[i + 1].Index : pdfText.Length;
            var blockText = pdfText[start..end].Trim();
            bool isContinuation = headerMatches[i].Value.Contains("continued", StringComparison.OrdinalIgnoreCase);

            var phoneMatch = PhonePattern.Match(blockText);
            if (!phoneMatch.Success) continue;

            string phone = phoneMatch.Groups[1].Value;
            string employeeName = ExtractEmployeeName(blockText, isContinuation);

            if (isContinuation && blocks.Count > 0)
            {
                var last = blocks[^1];
                if (last.PhoneNumber == phone)
                {
                    last.FullText += "\n" + blockText;
                    continue;
                }
            }

            blocks.Add(new LineBlock
            {
                PhoneNumber = phone,
                EmployeeName = employeeName,
                FullText = blockText,
            });
        }

        return blocks;
    }

    private static string ExtractEmployeeName(string blockText, bool isContinuation)
    {
        var lines = blockText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return string.Empty;

        string candidate = lines[0].Trim();

        if (isContinuation)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Invoice:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Account:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Billing period:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Due date:", StringComparison.OrdinalIgnoreCase) ||
                    PhonePattern.IsMatch(trimmed) ||
                    trimmed.All(c => char.IsDigit(c) || c == '.' || c == ' '))
                    continue;

                if (!trimmed.StartsWith("$") && !trimmed.StartsWith("-$"))
                {
                    candidate = trimmed;
                    break;
                }
            }
        }

        if (Regex.IsMatch(candidate, @"Billing period:", RegexOptions.IgnoreCase))
            candidate = Regex.Replace(candidate, @"\s*Billing period:.*", "", RegexOptions.IgnoreCase).Trim();
        if (Regex.IsMatch(candidate, @"Due date:", RegexOptions.IgnoreCase))
            candidate = Regex.Replace(candidate, @"\s*Due date:.*", "", RegexOptions.IgnoreCase).Trim();

        return candidate;
    }

    #endregion

    #region Charge Extraction (with section-aware state machine)

    /// <summary>
    /// Extracts charge line items from a per-line block using section-aware parsing.
    /// Handles Equipment section split-line format where description and amount are on separate lines.
    /// Filters informational Device Payment Plan lines (Paid, Past Due, Balance).
    /// Allows "$X Off" discount descriptions.
    /// </summary>
    private static void ExtractChargeItems(LineBlock block, List<ParsedCharge> charges,
        HashSet<string> seenKeys)
    {
        if (string.IsNullOrEmpty(block.PhoneNumber)) return;

        string phone = block.PhoneNumber;
        var lines = block.FullText.Split('\n');
        var currentSection = InvoiceSection.None;
        bool isEquipmentSection = false;
        string? pendingEquipmentDesc = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 3) continue;

            if (line.StartsWith("Total Current Charges", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = InvoiceSection.None;
                isEquipmentSection = false;
                pendingEquipmentDesc = null;
                continue;
            }

            var detected = DetectSection(line);
            if (detected != InvoiceSection.None)
            {
                currentSection = detected;
                isEquipmentSection = detected == InvoiceSection.Equipment;
                if (!isEquipmentSection) pendingEquipmentDesc = null;
                continue;
            }

            if (currentSection == InvoiceSection.None) continue;
            if (currentSection == InvoiceSection.UsageAndPurchase) continue;

            if (isEquipmentSection)
            {
                var standaloneMatch = StandaloneAmountPattern.Match(line);
                if (standaloneMatch.Success)
                {
                    if (pendingEquipmentDesc != null)
                    {
                        bool isNeg = standaloneMatch.Groups[1].Value == "-";
                        string amtStr = standaloneMatch.Groups[2].Value.Replace(",", "");
                        if (decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) && amt != 0)
                        {
                            if (isNeg) amt = -amt;
                            string cleanDesc = RemoveDateRange(pendingEquipmentDesc);
                            string key = BuildChargeKey(phone, cleanDesc, amt);
                            if (seenKeys.Add(key))
                            {
                                charges.Add(new ParsedCharge
                                {
                                    ChargeDescription = cleanDesc,
                                    Amount = amt,
                                    Line = phone,
                                });
                            }
                        }
                    }
                    pendingEquipmentDesc = null;
                    continue;
                }
            }

            var m = ChargeLineItemPattern.Match(line);
            if (!m.Success)
            {
                if (isEquipmentSection && line.Length >= 5
                    && !ChargeSkipPrefixes.Any(skip => line.StartsWith(skip, StringComparison.OrdinalIgnoreCase))
                    && !line.StartsWith("(") && !line.StartsWith("$"))
                {
                    pendingEquipmentDesc = line;
                }
                continue;
            }

            pendingEquipmentDesc = null;

            string desc = m.Groups[1].Value.Trim();
            bool isNegative = m.Groups[2].Value == "-";
            string amtStr2 = m.Groups[3].Value.Replace(",", "");

            if (ChargeSkipPrefixes.Any(skip => desc.StartsWith(skip, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (ChargeSkipExact.Contains(desc))
                continue;

            if (Regex.IsMatch(desc, @"^Total\s+", RegexOptions.IgnoreCase))
                continue;

            if (IsInformationalText(desc))
                continue;

            if (desc.StartsWith("(") || desc.Length < 3)
                continue;

            if (desc.StartsWith("$") && !DollarOffPattern.IsMatch(desc))
                continue;

            if (!decimal.TryParse(amtStr2, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                continue;

            if (amount == 0) continue;

            if (isNegative) amount = -amount;

            if (isEquipmentSection)
                desc = RemoveDateRange(desc);

            string key2 = BuildChargeKey(phone, desc, amount);
            if (!seenKeys.Add(key2)) continue;

            charges.Add(new ParsedCharge
            {
                ChargeDescription = desc,
                Amount = amount,
                Line = phone,
            });
        }
    }

    #endregion

    #region Account-Level Charges (from reference project's ParseAccountLevelCharges)

    /// <summary>
    /// Extracts charges from the "Account Level Charges Details" section.
    /// These are account-wide charges not tied to a specific phone line.
    /// Adapted from the reference project's ParseAccountLevelCharges method.
    /// </summary>
    private static void ExtractAccountLevelCharges(string pdfText, List<ParsedCharge> charges,
        HashSet<string> seenKeys)
    {
        var startMatch = AccountLevelStartPattern.Match(pdfText);
        if (!startMatch.Success) return;

        int startIdx = startMatch.Index + startMatch.Length;

        var endPatterns = new[]
        {
            "Usage and Purchase Charges",
            "Charges by line details",
        };
        int endIdx = pdfText.Length;
        foreach (var pattern in endPatterns)
        {
            int idx = pdfText.IndexOf(pattern, startIdx, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx < endIdx)
                endIdx = idx;
        }

        string sectionText = pdfText[startIdx..endIdx];
        var lines = sectionText.Split('\n');

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 3) continue;

            var m = ChargeLineItemPattern.Match(line);
            if (!m.Success) continue;

            string desc = m.Groups[1].Value.Trim();
            bool isNegative = m.Groups[2].Value == "-";
            string amtStr = m.Groups[3].Value.Replace(",", "");

            if (ChargeSkipPrefixes.Any(skip => desc.StartsWith(skip, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (desc.StartsWith("(") || desc.StartsWith("$") || desc.Length < 3)
                continue;

            if (!decimal.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                continue;

            if (amount == 0) continue;
            if (isNegative) amount = -amount;

            string key = BuildChargeKey("ACCOUNT", desc, amount);
            if (!seenKeys.Add(key)) continue;

            charges.Add(new ParsedCharge
            {
                ChargeDescription = desc,
                Amount = amount,
                Line = null,
            });
        }
    }

    #endregion

    #region Overview Table Parsing (from reference project's ParserOverviewLine)

    /// <summary>
    /// Parses the "Account Charges and Line Charges" summary table to extract
    /// per-line surcharges and taxes that may not appear in the "Charges by line details" section.
    /// Adapted from the reference project's ParserOverviewLine concept.
    /// </summary>
    private static void ExtractOverviewSurcharges(string pdfText, ParsedInvoiceResult result)
    {
        var overviewMatch = OverviewTableStartPattern.Match(pdfText);
        if (!overviewMatch.Success) return;

        int startIdx = overviewMatch.Index + overviewMatch.Length;

        int endIdx = pdfText.IndexOf("Charges by line details", startIdx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0)
            endIdx = Math.Min(startIdx + 20000, pdfText.Length);

        string sectionText = pdfText[startIdx..endIdx];
        var existingPrevAdj = result.SummaryFields.GetValueOrDefault("prev_adj");

        var surchargeMatch = Regex.Match(sectionText,
            @"Surcharges\s+and\s+Other\s+charges?\s*&?\s*credits?\s+\$?([\d,]+\.\d{2})",
            RegexOptions.IgnoreCase);
        if (surchargeMatch.Success && string.IsNullOrWhiteSpace(existingPrevAdj))
        {
            result.SummaryFields["prev_adj"] = surchargeMatch.Groups[1].Value.Replace(",", "");
        }

        var taxMatch = Regex.Match(sectionText,
            @"Taxes,?\s*Governmental\s+Surcharges?\s*&?\s*Fees?\s+\$?([\d,]+\.\d{2})",
            RegexOptions.IgnoreCase);
        if (taxMatch.Success)
        {
            var existingTax = result.SummaryFields.GetValueOrDefault("curr_tax");
            if (string.IsNullOrWhiteSpace(existingTax))
                result.SummaryFields["curr_tax"] = taxMatch.Groups[1].Value.Replace(",", "");
        }
    }

    #endregion

    #region Usage Extraction

    private void ExtractUsageItems(LineBlock block, List<ParsedUsageItem> usages,
        HashSet<string> seenKeys)
    {
        string text = block.FullText;
        string phone = block.PhoneNumber ?? "";
        string employee = block.EmployeeName ?? "";

        string totalVoiceCost = ExtractTotalCost(text, TotalVoiceCostPattern);
        string totalDataCost = ExtractTotalCost(text, TotalDataCostPattern);
        string totalMsgCost = ExtractTotalCost(text, TotalMessagingCostPattern);

        foreach (Match m in VoiceUsagePattern.Matches(text))
        {
            string usocName = ExtractUsocDescription(m.Value);
            string key = BuildUsageKey(phone, usocName, "V");
            if (!seenKeys.Add(key)) continue;

            usages.Add(new ParsedUsageItem
            {
                LineNumber = phone,
                EmployeeName = employee,
                UsocName = usocName,
                UsageLimit = NormalizeLimit(m.Groups[1].Value),
                UsageAmount = m.Groups[2].Value.Replace(",", ""),
                Charge = totalVoiceCost,
                UsageType = "V",
            });
        }

        foreach (Match m in DataUsagePattern.Matches(text))
        {
            string usocDesc = m.Groups[1].Value.Trim();
            string key = BuildUsageKey(phone, usocDesc, "D");
            if (!seenKeys.Add(key)) continue;

            string unit = m.Groups[2].Value.Trim().ToLowerInvariant();
            string limit = NormalizeLimit(m.Groups[3].Value);
            string used = m.Groups[4].Value.Replace(",", "");

            decimal usedVal = ParseDecimalSafe(used);
            decimal limitVal = limit == "-1" ? -1 : ParseDecimalSafe(limit);

            usedVal = ConvertToGb(usedVal, unit);
            if (limitVal > 0) limitVal = ConvertToGb(limitVal, unit);

            usages.Add(new ParsedUsageItem
            {
                LineNumber = phone,
                EmployeeName = employee,
                UsocName = usocDesc,
                UsageLimit = limitVal == -1 ? "-1" : limitVal.ToString("F3", CultureInfo.InvariantCulture),
                UsageAmount = usedVal.ToString("F3", CultureInfo.InvariantCulture),
                Charge = totalDataCost,
                UsageType = "D",
            });
        }

        foreach (Match m in TextUsagePattern.Matches(text))
        {
            string usocName = m.Groups[1].Value.Trim();
            string key = BuildUsageKey(phone, usocName, "T");
            if (!seenKeys.Add(key)) continue;

            usages.Add(new ParsedUsageItem
            {
                LineNumber = phone,
                EmployeeName = employee,
                UsocName = usocName,
                UsageLimit = NormalizeLimit(m.Groups[2].Value),
                UsageAmount = m.Groups[3].Value.Replace(",", ""),
                Charge = totalMsgCost,
                UsageType = "T",
            });
        }

        if (Regex.IsMatch(text, @"Roaming", RegexOptions.IgnoreCase))
        {
            var roamingPattern = new Regex(
                @"(Roaming[^\r\n]*?)(?:\s*\(\d{2}/\d{2}\s*-\s*\d{2}/\d{2}\))?\s+" +
                @"(?:minutes|messages|gigabytes|megabytes|kilobytes)\s+(unlimited|\d[\d,.]*)\s+([\d,.]+)\s+([\-\$\d,.]+)\s+([\-\$\d,.]+)",
                RegexOptions.IgnoreCase);

            foreach (Match m in roamingPattern.Matches(text))
            {
                string usocName = m.Groups[1].Value.Trim();
                string key = BuildUsageKey(phone, usocName, "R");
                if (!seenKeys.Add(key)) continue;

                usages.Add(new ParsedUsageItem
                {
                    LineNumber = phone,
                    EmployeeName = employee,
                    UsocName = usocName,
                    UsageLimit = NormalizeLimit(m.Groups[2].Value),
                    UsageAmount = m.Groups[3].Value.Replace(",", ""),
                    Charge = CleanCost(m.Groups[5].Value),
                    UsageType = "R",
                });
            }
        }
    }

    private static string ExtractUsocDescription(string matchValue)
    {
        var m = Regex.Match(matchValue,
            @"((?:Calling\s+Plan|Previous\s+Calling\s+Plan|New\s+Calling\s+Plan)(?:\s*\(\d{2}/\d{2}\s*-\s*\d{2}/\d{2}\))?)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : matchValue.Trim();
    }

    private static string ExtractTotalCost(string text, Regex pattern)
    {
        var m = pattern.Match(text);
        return m.Success ? CleanCost(m.Groups[1].Value) : "0.00";
    }

    #endregion

    #region Inventory Extraction (with Previous/New Plan handling from reference)

    /// <summary>
    /// Extracts inventory (plan info) for a phone line.
    /// Handles Previous/New Plan transitions, refund-only lines (disconnected services),
    /// and filters out informational text (pricing descriptions, URLs, status messages).
    /// </summary>
    private ParsedInventoryItem? ExtractInventory(LineBlock block)
    {
        if (string.IsNullOrEmpty(block.PhoneNumber)) return null;

        string text = block.FullText;
        var lines = text.Split('\n');
        bool inMonthlyCharges = false;
        bool inPlan = false;
        bool isPreviousPlan = false;
        string? planName = null;
        string? planAmount = null;
        string? refundPlanName = null;
        string? refundPlanAmount = null;
        string? deviceType = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();

            if (MonthlyChargesPattern.IsMatch(line))
            {
                inMonthlyCharges = true;
                continue;
            }

            if (inMonthlyCharges && !inPlan)
            {
                if (line.Equals("Plan", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Plan ", StringComparison.OrdinalIgnoreCase))
                {
                    inPlan = true;
                    continue;
                }
            }

            if (inMonthlyCharges &&
                (line.StartsWith("Features", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Usage and Purchase", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Surcharges", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Other Charges and Credits", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Taxes, Governmental", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Equipment Charges", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Total Current Charges", StringComparison.OrdinalIgnoreCase)))
                break;

            if (inMonthlyCharges && inPlan && planName == null)
            {
                if (line.StartsWith("PreviousPlan", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Previous Plan", StringComparison.OrdinalIgnoreCase))
                {
                    isPreviousPlan = true;
                    continue;
                }

                if (line.StartsWith("NewPlan", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("New Plan", StringComparison.OrdinalIgnoreCase))
                {
                    isPreviousPlan = false;
                    continue;
                }

                if (isPreviousPlan) continue;

                if (line.StartsWith("MonthinAdvance", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Month in Advance", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("(Normal", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Includes proration", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Beginning on", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Plan from", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("Service suspended", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Service disconnected", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsInformationalText(line))
                    continue;

                if (line.StartsWith("$") && line.Contains(" per ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (DollarOffPattern.IsMatch(line))
                    continue;

                var planMatch = PlanLinePattern.Match(line);
                if (planMatch.Success)
                {
                    string candidateName = planMatch.Groups[1].Value.Trim();
                    string sign = planMatch.Groups[2].Value;
                    string candidateAmount = sign + planMatch.Groups[3].Value.Trim();

                    if (SectionHeaders.Contains(candidateName))
                        continue;
                    if (IsInformationalText(candidateName))
                        continue;

                    if (candidateName.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                        candidateName.Contains("Reversal", StringComparison.OrdinalIgnoreCase))
                    {
                        refundPlanName ??= candidateName;
                        refundPlanAmount ??= candidateAmount;
                        continue;
                    }

                    planName = candidateName;
                    planAmount = candidateAmount;
                    break;
                }

                if (Regex.IsMatch(line, @"\$[\d,.]+"))
                {
                    var amtMatch = Regex.Match(line, @"^(.+?)\s+(-?)\.?\$([\d,.]+)(?:\s.*)?$");
                    if (amtMatch.Success)
                    {
                        string candidateName = amtMatch.Groups[1].Value.Trim();

                        if (SectionHeaders.Contains(candidateName))
                            continue;
                        if (IsInformationalText(candidateName))
                            continue;
                        if (candidateName.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (candidateName.Length < 5)
                            continue;

                        string candidateSign = amtMatch.Groups[2].Value;
                        string candidateAmount = candidateSign + amtMatch.Groups[3].Value.Trim();

                        if (candidateName.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                            candidateName.Contains("Reversal", StringComparison.OrdinalIgnoreCase))
                        {
                            refundPlanName ??= candidateName;
                            refundPlanAmount ??= candidateAmount;
                            continue;
                        }

                        planName = candidateName;
                        planAmount = candidateAmount;
                        break;
                    }
                }
            }
        }

        planName ??= refundPlanName;
        planAmount ??= refundPlanAmount;

        if (planName == null)
        {
            inMonthlyCharges = false;
            isPreviousPlan = false;
            refundPlanName = null;
            refundPlanAmount = null;

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (MonthlyChargesPattern.IsMatch(line))
                {
                    inMonthlyCharges = true;
                    continue;
                }

                if (inMonthlyCharges)
                {
                    if (line.StartsWith("Features", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Usage and Purchase", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Surcharges", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Other Charges and Credits", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Taxes, Governmental", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Equipment Charges", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Total Current Charges", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (line.StartsWith("PreviousPlan", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Previous Plan", StringComparison.OrdinalIgnoreCase))
                    {
                        isPreviousPlan = true;
                        continue;
                    }
                    if (line.StartsWith("NewPlan", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("New Plan", StringComparison.OrdinalIgnoreCase))
                    {
                        isPreviousPlan = false;
                        continue;
                    }
                    if (isPreviousPlan) continue;

                    if (DollarOffPattern.IsMatch(line))
                        continue;

                    if (IsInformationalText(line))
                        continue;

                    var planMatch = PlanLinePattern.Match(line);
                    if (planMatch.Success)
                    {
                        string name = planMatch.Groups[1].Value.Trim();

                        if (SectionHeaders.Contains(name))
                            continue;
                        if (IsInformationalText(name))
                            continue;

                        string sign = planMatch.Groups[2].Value;
                        string amount = sign + planMatch.Groups[3].Value.Trim();

                        if (name.Contains("Discount", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Reversal", StringComparison.OrdinalIgnoreCase))
                        {
                            refundPlanName ??= name;
                            refundPlanAmount ??= amount;
                            continue;
                        }

                        planName = name;
                        planAmount = amount;
                        break;
                    }
                }
            }

            planName ??= refundPlanName;
            planAmount ??= refundPlanAmount;
        }

        var deviceLines = lines.Take(10);
        foreach (var dl in deviceLines)
        {
            string d = dl.Trim();
            if (Regex.IsMatch(d, @"\b(Note\d|Galaxy|iPhone|Jetpack|Mifi|Pixel|iPad|Watch)\b", RegexOptions.IgnoreCase))
            {
                deviceType = d;
                break;
            }
        }

        return new ParsedInventoryItem
        {
            ReferenceNumber = block.PhoneNumber,
            EmployeeName = block.EmployeeName,
            PlanName = planName,
            PlanAmount = planAmount,
            ServiceType = deviceType,
        };
    }

    #endregion

    #region Summary Field Extraction

    private static void ExtractVerizonSummaryFields(string pdfText, ParsedInvoiceResult result)
    {
        var fields = result.SummaryFields;

        var currChgMatch = Regex.Match(pdfText,
            @"Total\s+Current\s+charges?\s+due\s+by\s+[\d/]+\s+\$?([\d,]+\.\d{2})",
            RegexOptions.IgnoreCase);
        if (!currChgMatch.Success)
        {
            currChgMatch = Regex.Match(pdfText,
                @"This\s+month'?s\s+charges\s+.*?\$?([\d,]+\.\d{2})",
                RegexOptions.IgnoreCase);
        }
        if (currChgMatch.Success)
        {
            fields["curr_chg"] = currChgMatch.Groups[1].Value.Replace(",", "");
        }

        if (!fields.ContainsKey("invoice_st_dtm") || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("invoice_st_dtm")))
        {
            var bpMatch = BillingPeriodPattern.Match(pdfText);
            if (bpMatch.Success)
            {
                string endDateStr = bpMatch.Groups[2].Value.Trim();
                string startPart = bpMatch.Groups[1].Value.Trim();

                if (DateTime.TryParse(endDateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDt))
                {
                    string startWithYear = startPart + ", " + endDt.Year;
                    if (DateTime.TryParse(startWithYear, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stDt))
                    {
                        if (stDt > endDt) stDt = stDt.AddYears(-1);
                        fields["invoice_st_dtm"] = stDt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    }

                    if (!fields.ContainsKey("invoice_end_dtm") || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("invoice_end_dtm")))
                    {
                        fields["invoice_end_dtm"] = endDt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        if (!fields.ContainsKey("beg_bal") || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("beg_bal")))
        {
            var m = Regex.Match(pdfText, @"Balance\s+from\s+last\s+bill\s+\$?([\d,]+\.\d{2})", RegexOptions.IgnoreCase);
            if (m.Success) fields["beg_bal"] = m.Groups[1].Value.Replace(",", "");
        }

        if (!fields.ContainsKey("payment") || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("payment")))
        {
            var m = Regex.Match(pdfText, @"Payments?\s*-\s*Thank\s+You\s+-?\$?([\d,]+\.\d{2})", RegexOptions.IgnoreCase);
            if (m.Success) fields["payment"] = m.Groups[1].Value.Replace(",", "");
        }

        if (!fields.ContainsKey("end_bal") || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("end_bal")))
        {
            var m = Regex.Match(pdfText, @"Total\s+due\s+\$?([\d,]+\.\d{2})", RegexOptions.IgnoreCase);
            if (m.Success) fields["end_bal"] = m.Groups[1].Value.Replace(",", "");
        }
    }

    #endregion

    #region Internal Types

    private class LineBlock
    {
        public string? PhoneNumber { get; set; }
        public string? EmployeeName { get; set; }
        public string FullText { get; set; } = string.Empty;
    }

    #endregion
}
