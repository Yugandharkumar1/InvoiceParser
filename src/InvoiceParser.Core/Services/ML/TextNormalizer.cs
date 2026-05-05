using System.Text.RegularExpressions;

namespace InvoiceParser.Core.Services.ML;

public static class TextNormalizer
{
    private static readonly Regex NonAlphanumericPattern = new(@"[^a-z0-9\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpacePattern = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var result = input.ToLowerInvariant().Trim();
        result = NonAlphanumericPattern.Replace(result, "");
        result = MultiSpacePattern.Replace(result, " ").Trim();
        return result;
    }

    public static double LevenshteinSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0.0;

        var distance = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - ((double)distance / maxLen);
    }

    private static int LevenshteinDistance(string source, string target)
    {
        var sourceLen = source.Length;
        var targetLen = target.Length;

        // Optimize: use single-row DP to reduce memory from O(m*n) to O(n)
        var previousRow = new int[targetLen + 1];
        var currentRow = new int[targetLen + 1];

        for (int j = 0; j <= targetLen; j++)
            previousRow[j] = j;

        for (int i = 1; i <= sourceLen; i++)
        {
            currentRow[0] = i;

            for (int j = 1; j <= targetLen; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(
                    Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1),
                    previousRow[j - 1] + cost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[targetLen];
    }
}
