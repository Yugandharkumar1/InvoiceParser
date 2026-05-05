using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Parsing;

/// <summary>
/// Extracts carrier account numbers including hyphenated formats (e.g. 123-456-7890).
/// </summary>
public static class AccountExtractor
{
    /// <summary>
    /// Account value capture: digits, hyphens, spaces (collapsed later), dots; not full line noise.
    /// </summary>
    private const string AccountBody = @"[\d\-\s\.\(\)]+";

    private static readonly (string Label, Regex Pattern)[] Patterns =
    {
        ("AccountNumberColon", new Regex($@"Account\s*Number\s*:\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountHash", new Regex($@"Account\s*#\s*:?\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountNo", new Regex($@"Account\s*No\.?\s*:?\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountNumberUpper", new Regex($@"ACCOUNT\s+NUMBER\s+({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountNumberLower", new Regex($@"Account\s*number\s*:\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountColon", new Regex($@"Account\s*:\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountNoUpper", new Regex($@"ACCOUNT\s+NO\s*:\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("MasterAccount", new Regex($@"(?:Master\s+)?Account\s+Number\s+({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("AccountNewline", new Regex($@"Account\s*\r?\n\s*({AccountBody})", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        // Digit-only fallback (strict) — kept after flexible patterns
        ("AccountDigitsOnly", new Regex(@"Account\s*Number\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    /// <summary>Try extract account; returns normalized compact form (hyphens preserved).</summary>
    public static bool TryExtract(string text, out string? account)
    {
        account = null;
        foreach (var (_, rx) in Patterns)
        {
            var m = rx.Match(text);
            if (!m.Success || m.Groups.Count < 2) continue;

            var raw = m.Groups[1].Value.Trim();
            raw = Regex.Replace(raw, @"\s+", "");
            raw = raw.Trim('(', ')', '.');
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < 3) continue;

            if (!Regex.IsMatch(raw, @"\d")) continue;

            account = raw;
            return true;
        }

        return false;
    }
}
