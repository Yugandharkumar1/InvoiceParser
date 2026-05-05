using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services;

/// <summary>
/// Three-tier charge description validation:
///   Tier 1 – Deterministic regex filters (URLs, help text, disclaimers, page markers)
///   Tier 2 – Learned skip rules from InvoiceFeedback / VendorParsingRule
///   Tier 3 – AI classification for remaining ambiguous descriptions
/// </summary>
public class ChargeValidationService
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly ILogger<ChargeValidationService> _logger;

    private static readonly Regex UrlPattern = new(
        @"(?:\.com|\.net|\.org|\.gov|www\.|https?://)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InformationalPattern = new(
        @"^(?:See\s|Visit\s|Go\s+to\s|Learn\s+more|For\s+(?:more|details|info)|Contact\s|Call\s+\d|Terms\s+and|©|\*{1,3}\s|Page\s+\d|continued\s+on)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DisclaimerPattern = new(
        @"(?:subject\s+to\s+change|not\s+responsible|all\s+rights\s+reserved|privacy\s+policy|terms\s+of\s+(?:service|use))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FooterPattern = new(
        @"(?:printed\s+on|generated\s+on|confidential|do\s+not\s+(?:pay|discard))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ChargeValidationService(HttpClient httpClient, OpenAiSettings settings,
        ILogger<ChargeValidationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public bool IsAiAvailable =>
        !string.IsNullOrWhiteSpace(_settings.BaseUrl) &&
        (_settings.BaseUrl.Contains("localhost") || _settings.BaseUrl.Contains("11434") ||
         !string.IsNullOrWhiteSpace(_settings.ApiKey));

    /// <summary>
    /// Validates charges through all three tiers and removes invalid ones.
    /// </summary>
    public async Task<int> ValidateAndFilterAsync(ParsedInvoiceResult parsed,
        List<string> learnedSkipPatterns)
    {
        int totalRemoved = 0;

        // Tier 1: deterministic regex
        int tier1 = parsed.Charges.RemoveAll(c => IsInvalidByRules(c.ChargeDescription));
        if (tier1 > 0)
            _logger.LogInformation("ChargeValidation Tier 1 (regex): removed {Count} invalid charge(s).", tier1);
        totalRemoved += tier1;

        // Tier 2: learned skip rules
        if (learnedSkipPatterns.Count > 0)
        {
            int tier2 = parsed.Charges.RemoveAll(c => MatchesLearnedSkip(c.ChargeDescription, learnedSkipPatterns));
            if (tier2 > 0)
                _logger.LogInformation("ChargeValidation Tier 2 (learned rules): removed {Count} charge(s).", tier2);
            totalRemoved += tier2;
        }

        // Tier 3: AI classification for remaining ambiguous descriptions
        if (IsAiAvailable)
        {
            var ambiguous = parsed.Charges
                .Where(c => IsAmbiguous(c.ChargeDescription))
                .ToList();

            if (ambiguous.Count > 0)
            {
                _logger.LogInformation("ChargeValidation Tier 3 (AI): classifying {Count} ambiguous charge(s).", ambiguous.Count);
                var invalidDescs = await ClassifyWithAiAsync(ambiguous);

                if (invalidDescs.Count > 0)
                {
                    int tier3 = parsed.Charges.RemoveAll(c =>
                        invalidDescs.Contains(c.ChargeDescription ?? "", StringComparer.OrdinalIgnoreCase));
                    if (tier3 > 0)
                        _logger.LogInformation("ChargeValidation Tier 3 (AI): removed {Count} charge(s).", tier3);
                    totalRemoved += tier3;
                }
            }
        }

        return totalRemoved;
    }

    #region Tier 1: Deterministic Validation

    internal static bool IsInvalidByRules(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return true;

        var desc = description.Trim();

        if (UrlPattern.IsMatch(desc)) return true;
        if (InformationalPattern.IsMatch(desc)) return true;
        if (DisclaimerPattern.IsMatch(desc)) return true;
        if (FooterPattern.IsMatch(desc)) return true;

        return false;
    }

    #endregion

    #region Tier 2: Learned Skip Rules

    private static bool MatchesLearnedSkip(string? description, List<string> skipPatterns)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;

        return skipPatterns.Any(pattern =>
        {
            try { return Regex.IsMatch(description, pattern, RegexOptions.IgnoreCase); }
            catch { return false; }
        });
    }

    #endregion

    #region Tier 3: AI Classification

    private static bool IsAmbiguous(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        var d = desc.Trim();

        if (d.Length > 80) return true;
        if (Regex.IsMatch(d, @"[.!?]{2,}")) return true;
        if (Regex.IsMatch(d, @"\b(?:please|thank|sorry|note:|important)\b", RegexOptions.IgnoreCase)) return true;
        if (!Regex.IsMatch(d, @"\$|charge|plan|fee|service|access|line|device|data|voice|text|roaming|discount|promo|credit",
            RegexOptions.IgnoreCase) && d.Split(' ').Length > 6) return true;

        return false;
    }

    private async Task<HashSet<string>> ClassifyWithAiAsync(List<ParsedCharge> charges)
    {
        var invalidDescs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var prompt = BuildClassificationPrompt(charges);
            var response = await CallApiAsync(prompt);

            if (string.IsNullOrWhiteSpace(response))
                return invalidDescs;

            invalidDescs = ParseClassificationResponse(response, charges);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("ChargeValidation AI request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChargeValidation AI classification failed.");
        }

        return invalidDescs;
    }

    private static string BuildClassificationPrompt(List<ParsedCharge> charges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a telecom invoice data quality validator.");
        sb.AppendLine();
        sb.AppendLine("Below is a list of charge descriptions extracted from a telecom invoice.");
        sb.AppendLine("For each one, classify whether it is:");
        sb.AppendLine("- \"valid\": A real billing charge (plan name, fee, surcharge, discount, credit, equipment charge, etc.)");
        sb.AppendLine("- \"invalid\": NOT a charge — informational text, URL, help message, footer, disclaimer, page header, marketing text, etc.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY a JSON array (no markdown, no code fences):");
        sb.AppendLine("[");
        sb.AppendLine("  { \"index\": 0, \"classification\": \"valid\" },");
        sb.AppendLine("  { \"index\": 1, \"classification\": \"invalid\" }");
        sb.AppendLine("]");
        sb.AppendLine();
        sb.AppendLine("CHARGE DESCRIPTIONS:");

        for (int i = 0; i < charges.Count; i++)
        {
            sb.AppendLine($"{i}: \"{charges[i].ChargeDescription}\" (${charges[i].Amount:F2})");
        }

        return sb.ToString();
    }

    private HashSet<string> ParseClassificationResponse(string responseText, List<ParsedCharge> charges)
    {
        var invalidDescs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var jsonText = responseText.Trim();

            var fencePattern = new Regex(@"^```(?:json)?\s*\n?(.*?)\n?\s*```$", RegexOptions.Singleline);
            var fenceMatch = fencePattern.Match(jsonText);
            if (fenceMatch.Success)
                jsonText = fenceMatch.Groups[1].Value.Trim();

            var arrStart = jsonText.IndexOf('[');
            var arrEnd = jsonText.LastIndexOf(']');
            if (arrStart < 0 || arrEnd < 0 || arrEnd <= arrStart)
                return invalidDescs;

            jsonText = jsonText[arrStart..(arrEnd + 1)];

            using var doc = JsonDocument.Parse(jsonText);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var idxProp)) continue;
                if (!item.TryGetProperty("classification", out var classProp)) continue;

                int idx = idxProp.GetInt32();
                string? classification = classProp.GetString();

                if (classification?.Equals("invalid", StringComparison.OrdinalIgnoreCase) == true
                    && idx >= 0 && idx < charges.Count
                    && !string.IsNullOrWhiteSpace(charges[idx].ChargeDescription))
                {
                    invalidDescs.Add(charges[idx].ChargeDescription!);
                    _logger.LogInformation("AI classified as invalid: \"{Desc}\"", charges[idx].ChargeDescription);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse AI classification response.");
        }

        return invalidDescs;
    }

    private async Task<string?> CallApiAsync(string prompt)
    {
        var isOllama = _settings.BaseUrl.Contains("localhost") ||
                       _settings.BaseUrl.Contains("127.0.0.1") ||
                       _settings.BaseUrl.Contains("11434");

        var systemMsg = "You are a telecom invoice data quality validator. Always respond with valid JSON only. Never include markdown code fences or commentary.";

        object requestBody;
        if (isOllama)
        {
            requestBody = new
            {
                model = _settings.Model,
                messages = new object[]
                {
                    new { role = "system", content = systemMsg },
                    new { role = "user", content = prompt }
                },
                temperature = 0,
                stream = false,
                options = new { num_ctx = 8192 }
            };
        }
        else
        {
            requestBody = new
            {
                model = _settings.Model,
                messages = new object[]
                {
                    new { role = "system", content = systemMsg },
                    new { role = "user", content = prompt }
                },
                temperature = 0,
                response_format = new { type = "json_object" }
            };
        }

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;

        if (!isOllama && !string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("ChargeValidation AI returned {StatusCode}: {Error}", (int)response.StatusCode, errorBody);
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            return choices[0].GetProperty("message").GetProperty("content").GetString();

        if (root.TryGetProperty("message", out var message))
            return message.GetProperty("content").GetString();

        return null;
    }

    #endregion
}
