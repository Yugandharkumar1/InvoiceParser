using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;

namespace InvoiceParser.Core.Services;

public class RuleLearningService
{
    private static readonly Dictionary<string, string> CapturePatterns = new()
    {
        ["invoice_number"] = @"\S+",
        ["carrier_account"] = @"\d+",
        ["invoice_date"] = @"[\d/\-]+",
        ["invoice_st_dtm"] = @"\d{1,2}/\d{1,2}/\d{2,4}",
        ["invoice_end_dtm"] = @"\d{1,2}/\d{1,2}/\d{2,4}",
        ["invoice_due_dtm"] = @"[\d/\-]+",
        ["beg_bal"] = @"-?\$?[\d,]+\.\d{2}",
        ["payment"] = @"-?\$?[\d,]+\.\d{2}",
        ["prev_adj"] = @"-?\$?[\d,]+\.\d{2}",
        ["curr_adj"] = @"-?\$?[\d,]+\.\d{2}",
        ["curr_chg"] = @"-?\$?[\d,]+\.\d{2}",
        ["curr_tax"] = @"-?\$?[\d,]+\.\d{2}",
        ["end_bal"] = @"-?\$?[\d,]+\.\d{2}",
    };

    public List<VendorParsingRule> GenerateRules(string pdfText,
        Dictionary<string, string?> confirmedFields, int carrierId)
    {
        var rules = new List<VendorParsingRule>();
        var sortOrder = 1;

        foreach (var (fieldName, value) in confirmedFields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var rule = TryBuildRule(pdfText, fieldName, value, carrierId, sortOrder);
            if (rule != null)
            {
                rules.Add(rule);
                sortOrder++;
            }
        }

        return rules;
    }

    private static VendorParsingRule? TryBuildRule(string pdfText, string fieldName,
        string value, int carrierId, int sortOrder)
    {
        var lines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        var cleanedValue = value.Replace("$", "").Replace(",", "").Trim();
        var searchValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value, cleanedValue };

        VendorParsingRule? bestRule = null;
        int bestLabelLength = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            foreach (var sv in searchValues)
            {
                var idx = line.IndexOf(sv, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                var label = line[..idx].TrimEnd('$', ' ');
                if (label.Length < 3 || !Regex.IsMatch(label, @"[a-zA-Z]{2,}")) continue;

                var regexPattern = BuildRegexFromLabel(label, fieldName);
                if (regexPattern == null) continue;

                if (!VerifyPattern(pdfText, regexPattern, cleanedValue)) continue;

                if (label.Length > bestLabelLength)
                {
                    bestLabelLength = label.Length;
                    bestRule = new VendorParsingRule
                    {
                        CarrierId = carrierId,
                        FieldName = fieldName,
                        RegexPattern = regexPattern,
                        FieldType = GetFieldType(fieldName),
                        TargetTable = "t_invoice",
                        SortOrder = sortOrder,
                        IsActive = true,
                        SuccessCount = 1,
                    };
                }
            }
        }

        return bestRule;
    }

    private static string? BuildRegexFromLabel(string label, string fieldName)
    {
        try
        {
            var escaped = Regex.Escape(label.Trim());
            var flexible = Regex.Replace(escaped, @"(\\ )+", @"\s+");

            if (flexible.EndsWith(@"\:"))
                flexible += @"\s*";
            else
                flexible += @"\s+";

            if (!CapturePatterns.TryGetValue(fieldName, out var capturePattern))
                capturePattern = @"\S+";

            return $@"{flexible}\$?\s*({capturePattern})";
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyPattern(string pdfText, string regexPattern, string expectedCleanValue)
    {
        try
        {
            var match = Regex.Match(pdfText, regexPattern, RegexOptions.IgnoreCase);
            if (!match.Success || match.Groups.Count < 2) return false;

            var extracted = match.Groups[1].Value.Trim().Replace("$", "").Replace(",", "");
            return extracted.Equals(expectedCleanValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetFieldType(string fieldName)
    {
        return fieldName switch
        {
            "invoice_date" or "invoice_st_dtm" or "invoice_end_dtm" or "invoice_due_dtm" => "date",
            "beg_bal" or "payment" or "prev_adj" or "curr_adj"
                or "curr_chg" or "curr_tax" or "end_bal" => "decimal",
            _ => "string",
        };
    }
}
