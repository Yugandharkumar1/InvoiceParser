using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Services;

/// <summary>Shared normalization for matching saved feedback to a newly parsed invoice.</summary>
public static class FeedbackFieldNormalizer
{
    /// <summary>Account key: letters and digits only (no dashes, spaces, etc.).</summary>
    public static string NormalizeAccountKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return Regex.Replace(s.Trim(), @"[^A-Za-z0-9]", "", RegexOptions.CultureInvariant);
    }

    /// <summary>Stable calendar date for equality checks (yyyy-MM-dd).</summary>
    public static bool TryNormalizeInvoiceDateKey(string? dateStr, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(dateStr)) return false;
        if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        key = dt.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }
}
