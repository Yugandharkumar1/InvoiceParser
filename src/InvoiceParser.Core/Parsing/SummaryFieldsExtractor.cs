using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Services;

namespace InvoiceParser.Core.Parsing;

/// <summary>
/// Summary fields other than <c>carrier_account</c> (see <see cref="AccountExtractor"/>) and <c>end_bal</c> (see <see cref="TotalExtractor"/>).
/// </summary>
public static class SummaryFieldsExtractor
{
    public static void Extract(string pdfText, ParsedInvoiceResult result)
    {
        var summaryPatterns = new Dictionary<string, string[]>
        {
            ["invoice_number"] = new[]
            {
                @"Invoice\s*Number\s*:\s*(\S+)",
                @"Invoice\s*#\s*:?\s*(\S+)",
                @"Invoice\s*No\.?\s*:?\s*(\S+)",
                @"Invoice\s*:\s*(\S+)",
                @"Bill\s*Number\s*:\s*(\S+)",
                @"Invoice\s+Number\s+(\S+)",
            },
            ["invoice_date"] = new[]
            {
                @"Invoice\s*Date\s*:\s*([\d/\-]+)",
                @"Issue\s*Date\s*:\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Bill\s*Date\s*:\s*([\d/\-]+)",
                @"Statement\s*Date\s*:\s*([\d/\-]+)",
                @"Bill\s*Date\s*:?\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Invoice\s+Date\s+([\d/\-]+)",
                @"INVOICE\s+DATE\s*\r?\n\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"BILL\s+DATE\s*:\s*([\d/\-]+)",
                @"Bill\s+At\s+A\s+Glance\s+([\d/\-]+)",
                @"Bill\s+Date\s+([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
            },
            ["invoice_due_dtm"] = new[]
            {
                @"Due\s*Date\s*:\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"Payment\s*Due\s*:\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"Due\s*Date\s*:\s*(\d{1,2}-\d{1,2}-\d{2,4})",
                @"Due\s*Date\s*:\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Payment\s*Due\s*Date\s*:\s*([\d/\-]+)",
                @"Pay\s*by\s*:\s*([\d/\-]+)",
                @"Due\s+on\s+(\d{1,2}/\d{1,2}/\d{2,4})",
                @"[Dd]ue\s+by\s+([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Due\s+by\s+(\d{1,2}/\d{1,2}/\d{2,4})",
                @"Total\s+Amount\s+Due\s+by\s+([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Payment\s+Due\s*\r?\n\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"Payment\s+Due.*?\r?\n\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"Due\s+date:\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"DUE\s+DATE\s*\r?\n\s*(\d{1,2}/\d{1,2}/\d{2,4})",
                @"DUE\s+DATE\s+(\d{1,2}/\d{1,2}/\d{2,4})",
            },
            ["invoice_st_dtm"] = new[]
            {
                @"(?:activity|period|service)\s+from\s+(\d{1,2}/\d{1,2}/\d{2,4})",
                @"(?:Billing|Statement)\s*Period\s*:\s*([\d/\-]+)",
                @"Service\s*From\s*:\s*([\d/\-]+)",
                @"(?:activity|period|service)\s+from\s+([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"(?:Billing|Statement)\s*Period\s*:\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Bill\s+period\s*\r?\n\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
            },
            ["invoice_end_dtm"] = new[]
            {
                @"(?:through|thru|to)\s+(\d{1,2}/\d{1,2}/\d{2,4})",
                @"Service\s*(?:To|Through)\s*:\s*([\d/\-]+)",
                @"(?:through|thru|to)\s+([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Bill\s+period\s*\r?\n\s*[A-Za-z]+\s+\d{1,2},?\s+\d{4}\s*-\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
                @"Billing\s+period:\s+[A-Za-z]+\s+\d{1,2}\s*-\s*([A-Za-z]+\s+\d{1,2},?\s+\d{4})",
            },
            ["beg_bal"] = new[]
            {
                @"Previous\s+Statement\s+Balance\s+\$?(-?[\d,]+\.\d{2})",
                @"Previous\s+Balance\s+\$?(-?[\d,]+\.\d{2})",
                @"Prior\s+Balance\s+\$?(-?[\d,]+\.\d{2})",
                @"Beginning\s+Balance\s+\$?(-?[\d,]+\.\d{2})",
                @"Your\s+last\s+bill\s+\$?(-?[\d,]+\.\d{2})",
                @"Previous\s+bill\s+\$?(-?[\d,]+\.\d{2})",
                @"Last\s+Statement\s+Balance\s+\$?(-?[\d,]+\.\d{2})",
                @"BALANCE\s+FROM\s+LAST\s+BILLING\s+\$?(-?[\d,]+\.\d{2})",
                @"PREVIOUS\s+BALANCE\s+DUE\s+\$?(-?[\d,]+\.\d{2})",
                @"last\s+bill\s+of\s+\$?(-?[\d,]+\.\d{2})",
                @"Previous\s+Bill\s+Payment/Adj.*?\r?\n\s*\$?(-?[\d,]+\.\d{2})\s",
                @"Balance\s+from\s+last\s+bill\s+\$?(-?[\d,]+\.\d{2})",
            },
            ["payment"] = new[]
            {
                @"Previous\s+Bill\s+Payment/Adj.*?\r?\n\s*\$?[\d,]+\.\d{2}\s+\$?([\d,]+\.\d{2})(?:CR)?",
                @"Payment/Adj\s+Current.*?\r?\n.*?\$?([\d,]+\.\d{2})(?:CR)?",
                @"PAYMENT\(S\)\s+RECEIVED[\s\S]*?\$?([\d,]+\.\d{2})(?:CR)?",
                @"Payment/Adj\s*\r?\n\s*\$?([\d,]+\.\d{2})(?:CR)?",
                @"Payments?\s+\$?-?([\d,]+\.\d{2})",
                @"Payment\s+Received\s+\$?-?([\d,]+\.\d{2})",
                @"Payment,.*?-?\$?([\d,]+\.\d{2})",
                @"Payments?\s+Received.*?\$?-?([\d,]+\.\d{2})",
                @"Payments?\s+Applied\s+\$?-?([\d,]+\.\d{2})",
                @"paying\s+your\s+last\s+bill\s+of\s+\$?([\d,]+\.\d{2})",
                @"Payments?\s*-\s*Thank\s+You\s+-?\$?([\d,]+\.\d{2})",
                @"Total\s+Payments?\s+-?\$?([\d,]+\.\d{2})",
            },
            ["prev_adj"] = new[]
            {
                @"Adjustments?\s+\$?(-?[\d,]+\.\d{2})",
                @"Credits?\s+\$?(-?[\d,]+\.\d{2})",
            },
            ["curr_adj"] = new[]
            {
                @"Current\s+Adjustments?\s+\$?(-?[\d,]+\.\d{2})",
            },
            ["curr_chg"] = new[]
            {
                @"Subtotal[ \t]+\$?(-?[\d,]+\.\d{2})",
                @"Sub\s*-?\s*total[ \t]+\$?(-?[\d,]+\.\d{2})",
                @"Current\s+Charges?\s+Subtotal\s+\$?(-?[\d,]+\.\d{2})",
                @"Total\s+Current\s+Charges?\s+\$?(-?[\d,]+\.\d{2})",
                @"This\s+month'?s\s+charges\s+.*?\$?(-?[\d,]+\.\d{2})",
                @"Total\s+Current\s+charges?\s+due\s+by\s+[\d/]+\s+\$?(-?[\d,]+\.\d{2})",
                @"New\s+Charges?\s+\$?(-?[\d,]+\.\d{2})",
                @"Monthly\s+Charges?\s+\$?(-?[\d,]+\.\d{2})",
                @"Total\s+Charges\s+\$?(-?[\d,]+\.\d{2})",
                @"CURRENT\s+BILLING(?:\s+AMOUNT)?[ \t]+\$?(-?[\d,]+\.\d{2})",
                @"Current\s+Charges\s+-\s+Due\s+on\s+[\d/]+\s+\$?(-?[\d,]+\.\d{2})",
                @"Current\s+Billing\s+Total\s+Due\s*\r?\n\s*\$?[\d,]+\.\d{2}\s+\$?[\d,]+\.\d{2}(?:CR)?\s+\$?(-?[\d,]+\.\d{2})",
            },
            ["curr_tax"] = new[]
            {
                @"Taxes,?\s*Fees\s*(?:&|and)\s*Surcharges?\s+\$?(-?[\d,]+\.\d{2})",
                @"Total\s+Taxes?\s+\$?(-?[\d,]+\.\d{2})",
                @"Taxes?\s+and\s+(?:Fees|Surcharges)\s+\$?(-?[\d,]+\.\d{2})",
                @"Government\s+(?:Taxes|Fees).*?\$?(-?[\d,]+\.\d{2})",
                @"Taxes?\s+&\s+Surcharges?\s+\$?(-?[\d,]+\.\d{2})",
            },
        };

        foreach (var kvp in summaryPatterns)
        {
            foreach (var pattern in kvp.Value)
            {
                var match = Regex.Match(pdfText, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    result.SummaryFields[kvp.Key] = match.Groups[1].Value.Trim();
                    break;
                }
            }
        }

        if (result.SummaryFields.TryGetValue("payment", out var pmtVal) && pmtVal != null)
        {
            var cleaned = pmtVal.Replace("-", "").Replace("CR", "").Replace("cr", "")
                .Replace("(", "").Replace(")", "").Replace("$", "").Replace(",", "").Trim();
            if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var pmtDec))
                result.SummaryFields["payment"] = pmtDec.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
