using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Services;

/// <summary>
/// Evaluates a single configurable condition against a line of text or a numeric value.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// Evaluate <paramref name="conditionType"/> against <paramref name="subject"/> using <paramref name="conditionValue"/>.
    /// </summary>
    /// <param name="subject">The text or value to test (typically a single line from the PDF).</param>
    /// <param name="conditionType">
    /// equals, not_equals, contains, does_not_contain, starts_with, ends_with,
    /// regex_match, greater_than, less_than, is_empty, is_not_empty.
    /// </param>
    /// <param name="conditionValue">The value or pattern to compare against.</param>
    public static bool Evaluate(string subject, string conditionType, string? conditionValue)
    {
        var cv = conditionValue ?? string.Empty;

        return conditionType.ToLowerInvariant() switch
        {
            "equals"           => subject.Equals(cv, StringComparison.OrdinalIgnoreCase),
            "not_equals"       => !subject.Equals(cv, StringComparison.OrdinalIgnoreCase),
            "contains"         => subject.Contains(cv, StringComparison.OrdinalIgnoreCase),
            "does_not_contain" => !subject.Contains(cv, StringComparison.OrdinalIgnoreCase),
            "starts_with"      => subject.StartsWith(cv, StringComparison.OrdinalIgnoreCase),
            "ends_with"        => subject.EndsWith(cv, StringComparison.OrdinalIgnoreCase),
            "is_empty"         => string.IsNullOrWhiteSpace(subject),
            "is_not_empty"     => !string.IsNullOrWhiteSpace(subject),
            "greater_than"     => NumericCompare(subject, cv) > 0,
            "less_than"        => NumericCompare(subject, cv) < 0,
            "regex_match" or _ => RegexMatch(subject, cv),
        };
    }

    private static bool RegexMatch(string subject, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try { return Regex.IsMatch(subject, pattern, RegexOptions.IgnoreCase); }
        catch { return false; }
    }

    private static int NumericCompare(string subject, string conditionValue)
    {
        if (!decimal.TryParse(subject.Replace("$", "").Replace(",", "").Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var a)) return 0;
        if (!decimal.TryParse(conditionValue.Replace("$", "").Replace(",", "").Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var b)) return 0;
        return a.CompareTo(b);
    }
}
