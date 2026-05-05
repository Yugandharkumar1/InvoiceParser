using System.Text;
using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Parsing;

/// <summary>
/// Normalizes invoice PDF/OCR text before rule-based extraction (whitespace, line noise).
/// </summary>
public static class InvoiceTextNormalizer
{
    /// <summary>Horizontal whitespace (space, tab, Unicode space separators). .NET Regex has no \h.</summary>
    private static readonly Regex MultiSpace = new(@"[\t\p{Zs}]+", RegexOptions.Compiled);
    private static readonly Regex MultiBlankLine = new(@"(?:\r?\n){3,}", RegexOptions.Compiled);
    private static readonly Regex LineTrailingSpaces = new(@"[ \t]+\r?\n", RegexOptions.Compiled);

    /// <summary>
    /// Collapses horizontal whitespace, trims lines, reduces excessive blank lines.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var trimmed = MultiSpace.Replace(line, " ").TrimEnd();
            trimmed = trimmed.TrimStart();
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(trimmed);
        }

        var s = sb.ToString();
        s = MultiBlankLine.Replace(s, "\n\n");
        s = LineTrailingSpaces.Replace(s, "\n");
        return s;
    }
}
