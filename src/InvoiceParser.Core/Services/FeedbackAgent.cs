using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services;

public class FeedbackAgent
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly ILogger<FeedbackAgent> _logger;

    public FeedbackAgent(HttpClient httpClient, OpenAiSettings settings, ILogger<FeedbackAgent> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_settings.BaseUrl) &&
        (_settings.BaseUrl.Contains("localhost") || _settings.BaseUrl.Contains("11434") ||
         !string.IsNullOrWhiteSpace(_settings.ApiKey));

    public async Task<List<VendorParsingRule>> ProcessFeedbackAsync(InvoiceFeedback feedback)
    {
        var rules = new List<VendorParsingRule>();

        if (!IsAvailable)
        {
            _logger.LogInformation("FeedbackAgent not available (no LLM configured). Skipping.");
            return rules;
        }

        if (string.IsNullOrWhiteSpace(feedback.FeedbackText) || string.IsNullOrWhiteSpace(feedback.PdfText))
            return rules;

        try
        {
            var pdfSnippet = feedback.PdfText.Length > 4000
                ? feedback.PdfText[..4000]
                : feedback.PdfText;

            var prompt = BuildPrompt(feedback.FeedbackText, pdfSnippet, feedback.OriginalFieldsJson);

            _logger.LogInformation("FeedbackAgent sending feedback to LLM for analysis...");
            var response = await CallLlmAsync(prompt);

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("FeedbackAgent received empty response from LLM.");
                return rules;
            }

            _logger.LogInformation("FeedbackAgent received response ({Length} chars). Parsing rules...", response.Length);
            rules = ParseRulesFromResponse(response, feedback.CarrierId);

            // Validate each rule against the PDF text
            var validRules = new List<VendorParsingRule>();
            foreach (var rule in rules)
            {
                if (ValidateRule(rule, feedback.PdfText))
                {
                    validRules.Add(rule);
                    _logger.LogInformation(
                        "FeedbackAgent generated valid rule: {Field} = '{Pattern}'",
                        rule.FieldName, rule.RegexPattern);
                }
                else
                {
                    _logger.LogWarning(
                        "FeedbackAgent rule failed validation: {Field} = '{Pattern}'",
                        rule.FieldName, rule.RegexPattern);
                }
            }

            _logger.LogInformation(
                "FeedbackAgent: {Valid}/{Total} rules passed validation.",
                validRules.Count, rules.Count);

            return validRules;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("FeedbackAgent LLM request timed out.");
            return rules;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "FeedbackAgent could not reach LLM. Is Ollama running?");
            return rules;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FeedbackAgent failed.");
            return rules;
        }
    }

    private static string BuildPrompt(string feedbackText, string pdfSnippet, string? originalFieldsJson)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are an invoice parsing expert. A user has given feedback about incorrect data extraction from a PDF invoice.");
        sb.AppendLine();
        sb.AppendLine("USER FEEDBACK:");
        sb.AppendLine(feedbackText);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(originalFieldsJson))
        {
            sb.AppendLine("CURRENTLY PARSED VALUES:");
            sb.AppendLine(originalFieldsJson);
            sb.AppendLine();
        }

        sb.AppendLine("PDF TEXT (first portion):");
        sb.AppendLine(pdfSnippet);
        sb.AppendLine();

        sb.AppendLine(@"Based on the feedback and PDF text, generate regex patterns that would correctly extract the data the user is asking for.

Return a JSON array of rules. Each rule must have:
- ""fieldName"": the field to extract (use these exact names: invoice_number, carrier_account, invoice_date, invoice_due_dtm, invoice_st_dtm, invoice_end_dtm, beg_bal, payment, prev_adj, curr_adj, curr_chg, curr_tax, end_bal, line, charge_skip_pattern)
- ""regexPattern"": a C# regex pattern with exactly one capture group in parentheses for the value to extract
- ""targetTable"": ""t_invoice"" for summary fields, ""t_charge"" for charge-related rules
- ""fieldType"": ""string"", ""date"", ""decimal"", or ""skip""

Rules:
- The regex must be valid C# regex syntax
- Use exactly ONE capture group (parentheses) for the value to extract
- Escape special regex characters properly (use double backslashes for literal dots, parentheses, etc.)
- For skip rules (charge_skip_pattern), the regex should match text to remove from charge descriptions
- Test your regex mentally against the PDF text to ensure it matches
- Return ONLY the JSON array, no markdown, no code fences, no commentary

Example output:
[
  {
    ""fieldName"": ""payment"",
    ""regexPattern"": ""PAYMENT\\(S\\)\\s+RECEIVED.*?([\\d,]+\\.\\d{2})CR"",
    ""targetTable"": ""t_invoice"",
    ""fieldType"": ""decimal""
  }
]");

        return sb.ToString();
    }

    private async Task<string?> CallLlmAsync(string prompt)
    {
        var isOllama = _settings.BaseUrl.Contains("localhost") ||
                       _settings.BaseUrl.Contains("127.0.0.1") ||
                       _settings.BaseUrl.Contains("11434");

        object requestBody;

        if (isOllama)
        {
            requestBody = new
            {
                model = _settings.Model,
                messages = new object[]
                {
                    new { role = "system", content = "You are a regex expert for invoice parsing. Always respond with valid JSON only. Never include markdown code fences or commentary." },
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
                    new { role = "system", content = "You are a regex expert for invoice parsing. Always respond with valid JSON only." },
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
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("FeedbackAgent LLM returned {StatusCode}: {Error}", (int)response.StatusCode, errorBody);
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

    private List<VendorParsingRule> ParseRulesFromResponse(string responseText, int carrierId)
    {
        var rules = new List<VendorParsingRule>();

        try
        {
            var jsonText = responseText.Trim();

            // Strip markdown fences if present
            var fencePattern = new Regex(@"^```(?:json)?\s*\n?(.*?)\n?\s*```$", RegexOptions.Singleline);
            var fenceMatch = fencePattern.Match(jsonText);
            if (fenceMatch.Success)
                jsonText = fenceMatch.Groups[1].Value.Trim();

            // Find the JSON array
            var arrStart = jsonText.IndexOf('[');
            var arrEnd = jsonText.LastIndexOf(']');
            if (arrStart < 0 || arrEnd < 0 || arrEnd <= arrStart)
            {
                _logger.LogWarning("No JSON array found in LLM response.");
                return rules;
            }
            jsonText = jsonText[arrStart..(arrEnd + 1)];

            using var doc = JsonDocument.Parse(jsonText);
            var sortOrder = 50;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var fieldName = item.TryGetProperty("fieldName", out var fn) ? fn.GetString() : null;
                var regexPattern = item.TryGetProperty("regexPattern", out var rp) ? rp.GetString() : null;
                var targetTable = item.TryGetProperty("targetTable", out var tt) ? tt.GetString() : "t_invoice";
                var fieldType = item.TryGetProperty("fieldType", out var ft) ? ft.GetString() : "string";

                if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(regexPattern))
                    continue;

                // Verify the regex compiles
                try { _ = new Regex(regexPattern); }
                catch
                {
                    _logger.LogWarning("FeedbackAgent: invalid regex '{Pattern}' for field '{Field}'", regexPattern, fieldName);
                    continue;
                }

                rules.Add(new VendorParsingRule
                {
                    CarrierId = carrierId,
                    FieldName = fieldName!,
                    RegexPattern = regexPattern!,
                    TargetTable = targetTable ?? "t_invoice",
                    FieldType = fieldType ?? "string",
                    SortOrder = sortOrder++,
                    IsActive = true,
                    SuccessCount = 1,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FeedbackAgent failed to parse LLM response.");
        }

        return rules;
    }

    private bool ValidateRule(VendorParsingRule rule, string pdfText)
    {
        try
        {
            var match = Regex.Match(pdfText, rule.RegexPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success || match.Groups.Count < 2)
                return false;

            var captured = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(captured) || captured.Length > 500)
                return false;

            _logger.LogDebug(
                "FeedbackAgent validation: {Field} pattern captured '{Value}'",
                rule.FieldName, captured);

            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
