using System.Globalization;

namespace InvoiceParser.Core.Services;

public static class MonetaryParser
{
    public static bool TryParse(string? value, out decimal result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var cleaned = value
            .Replace("$", "").Replace(",", "")
            .Replace("CR", "").Replace("cr", "")
            .Replace("(", "").Replace(")", "")
            .Trim();

        bool isNegative = cleaned.StartsWith("-");
        cleaned = cleaned.Replace("-", "");

        if (string.IsNullOrWhiteSpace(cleaned)) return false;
        if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            return false;

        if (isNegative) result = -result;
        return true;
    }

    public static string Format(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return TryParse(value, out var d) ? Format(d) : value;
    }
}
