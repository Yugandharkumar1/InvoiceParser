using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Services;

/// <summary>
/// Applies a chain of <see cref="TransformStep"/> operations to a string value.
/// </summary>
public static class TransformPipeline
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Deserialize a JSON steps array stored on a rule. Returns null on failure.</summary>
    public static List<TransformStep>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<TransformStep>>(json, _json); }
        catch { return null; }
    }

    /// <summary>
    /// Apply every step in <paramref name="steps"/> to <paramref name="value"/> and return the result.
    /// Pass <paramref name="pdfText"/> and <paramref name="matchedLine"/> so that line-joining transforms
    /// (<c>join_next_line</c>, <c>join_prev_line</c>) can find adjacent lines in the source text.
    /// </summary>
    public static string Apply(string value, IEnumerable<TransformStep> steps,
        string? pdfText = null, string? matchedLine = null)
    {
        // Pre-split the PDF text into lines once (only when needed)
        string[]? lines = null;
        int matchedIdx  = -1;

        foreach (var step in steps)
        {
            if (string.IsNullOrEmpty(step.Type)) continue;

            // Lazy-initialise line array for join operations
            if ((step.Type == "join_next_line" || step.Type == "join_prev_line") &&
                lines == null && !string.IsNullOrEmpty(pdfText))
            {
                lines = pdfText.Split('\n');
                if (!string.IsNullOrEmpty(matchedLine))
                {
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (string.Equals(lines[i].TrimEnd(), matchedLine.TrimEnd(),
                            StringComparison.OrdinalIgnoreCase))
                        { matchedIdx = i; break; }
                    }
                }
            }

            value = ApplyStep(value, step, lines, matchedIdx);
        }
        return value;
    }

    private static string ApplyStep(string value, TransformStep step,
        string[]? lines = null, int matchedIdx = -1)
    {
        try
        {
            return step.Type.ToLowerInvariant() switch
            {
                "remove_prefix"   => RemovePrefix(value, step.Value1),
                "remove_suffix"   => RemoveSuffix(value, step.Value1),
                "replace"         => Replace(value, step.Value1, step.Value2),
                "trim"            => value.Trim(),
                "to_upper"        => value.ToUpperInvariant(),
                "to_lower"        => value.ToLowerInvariant(),
                "extract_between" => ExtractBetween(value, step.Value1, step.Value2),
                "regex_extract"   => RegexExtract(value, step.Value1),
                "convert_date"    => ConvertDate(value, step.Value1, step.Value2),
                "join_next_line"  => JoinAdjacentLine(value, step.Value1, lines, matchedIdx, +1),
                "join_prev_line"  => JoinAdjacentLine(value, step.Value1, lines, matchedIdx, -1),
                _                 => value,
            };
        }
        catch
        {
            return value;
        }
    }

    private static string RemovePrefix(string value, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return value;
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return value[prefix.Length..].TrimStart();
        return value;
    }

    private static string RemoveSuffix(string value, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return value;
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return value[..^suffix.Length].TrimEnd();
        return value;
    }

    private static string Replace(string value, string? oldText, string? newText)
    {
        if (string.IsNullOrEmpty(oldText)) return value;
        return value.Replace(oldText, newText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBetween(string value, string? start, string? end)
    {
        if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end)) return value;
        var si = value.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (si < 0) return value;
        si += start.Length;
        var ei = value.IndexOf(end, si, StringComparison.OrdinalIgnoreCase);
        if (ei < 0) return value[si..].Trim();
        return value[si..ei].Trim();
    }

    private static string RegexExtract(string value, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return value;
        var m = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
        if (m.Success && m.Groups.Count > 1) return m.Groups[1].Value.Trim();
        if (m.Success) return m.Value.Trim();
        return value;
    }

    /// <summary>
    /// Appends (direction=+1) or prepends (direction=-1) the adjacent non-empty line from the PDF text
    /// to <paramref name="value"/>, joined with <paramref name="separator"/> (defaults to a space).
    /// If context lines are unavailable the original value is returned unchanged.
    /// </summary>
    private static string JoinAdjacentLine(string value, string? separator,
        string[]? lines, int matchedIdx, int direction)
    {
        if (lines == null || matchedIdx < 0) return value;

        var sep = separator ?? " ";

        // Walk in the given direction until we find a non-empty line
        for (int i = matchedIdx + direction;
             i >= 0 && i < lines.Length;
             i += direction)
        {
            var candidate = lines[i].Trim();
            if (candidate.Length == 0) continue;

            return direction > 0
                ? value.TrimEnd() + sep + candidate   // join_next_line
                : candidate + sep + value.TrimStart(); // join_prev_line
        }

        return value; // no adjacent non-empty line found
    }

    private static string ConvertDate(string value, string? inputFormat, string? outputFormat)
    {
        if (string.IsNullOrEmpty(outputFormat)) return value;

        var formats = inputFormat != null
            ? new[] { inputFormat }
            : new[] { "MM/dd/yy", "MM/dd/yyyy", "M/d/yy", "M/d/yyyy",
                      "MMMM d, yyyy", "MMMM dd, yyyy",
                      "MMM d, yyyy", "MMM dd, yyyy",
                      "yyyy-MM-dd", "MM-dd-yyyy", "dd/MM/yyyy" };

        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(value.Trim(), fmt,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt.ToString(outputFormat, CultureInfo.InvariantCulture);
            }
        }

        // Generic fallback
        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dtFallback))
        {
            return dtFallback.ToString(outputFormat, CultureInfo.InvariantCulture);
        }

        return value;
    }
}
