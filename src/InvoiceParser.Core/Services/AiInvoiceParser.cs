using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services;

public class AiInvoiceParser
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly ILogger<AiInvoiceParser> _logger;

    public AiInvoiceParser(HttpClient httpClient, OpenAiSettings settings, ILogger<AiInvoiceParser> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public async Task<ParsedInvoiceResult?> ParseAsync(string pdfText)
    {
        if (!IsConfigured)
        {
            _logger.LogInformation("AI parser not configured (no API key). Skipping.");
            return null;
        }

        try
        {
            _logger.LogInformation("Starting AI parse with model '{Model}' at '{BaseUrl}'",
                _settings.Model, _settings.BaseUrl);

            var prompt = BuildPrompt(pdfText);
            var responseContent = await CallApiAsync(prompt);

            if (responseContent == null)
            {
                _logger.LogWarning("AI API returned no content.");
                return null;
            }

            _logger.LogInformation("AI response received ({Length} chars). Parsing JSON...", responseContent.Length);
            var result = ParseResponse(responseContent);

            if (result == null)
                _logger.LogWarning("Failed to parse AI response into invoice data.");
            else
                _logger.LogInformation("AI extracted {Fields} fields, {Charges} charges, {Usages} usages, {Inventories} inventory items.",
                    result.SummaryFields.Count, result.Charges.Count, result.Usages.Count, result.Inventories.Count);

            return result;
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("AI request timed out. Consider increasing timeout or using a smaller/faster model.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI parsing failed with exception.");
            return null;
        }
    }

    private static string BuildPrompt(string pdfText)
    {
        return @"Extract all relevant information from the telecom/SaaS invoice text below.

Return a JSON object with this exact structure:
{
  ""summary"": {
    ""invoice_number"": ""string or null"",
    ""carrier_account"": ""string or null"",
    ""invoice_date"": ""MM/dd/yyyy or null"",
    ""invoice_due_dtm"": ""MM/dd/yyyy or null"",
    ""invoice_st_dtm"": ""MM/dd/yyyy or null"",
    ""invoice_end_dtm"": ""MM/dd/yyyy or null"",
    ""beg_bal"": ""numeric string like 123.45 or null"",
    ""payment"": ""numeric string like -123.45 or null"",
    ""prev_adj"": ""numeric string or null"",
    ""curr_adj"": ""numeric string or null"",
    ""curr_chg"": ""numeric string or null"",
    ""curr_tax"": ""numeric string or null"",
    ""end_bal"": ""numeric string or null""
  },
  ""charges"": [
    {
      ""description"": ""charge description"",
      ""amount"": 0.00,
      ""line"": ""phone number or circuit ID or null"",
      ""location"": ""service location or address or null""
    }
  ],
  ""usages"": [
    {
      ""line_number"": ""phone number like 916-298-6093"",
      ""employee_name"": ""person name associated with the line or null"",
      ""usage_type"": ""V for voice, D for data, T for text, R for roaming"",
      ""description"": ""usage plan or feature name"",
      ""usage_limit"": ""numeric limit or unlimited"",
      ""usage_amount"": ""numeric amount used"",
      ""charge"": ""cost as numeric string like 12.50""
    }
  ],
  ""inventory"": [
    {
      ""line_number"": ""phone number like 916-298-6093"",
      ""employee_name"": ""person name associated with the line or null"",
      ""plan_name"": ""service plan name or null"",
      ""plan_amount"": ""monthly plan cost as numeric string or null"",
      ""service_type"": ""device type like iPhone, Galaxy, etc. or null""
    }
  ]
}

Rules:
- All dates must be in MM/dd/yyyy format
- Monetary amounts should be plain numeric strings without $ signs or commas (e.g. ""1234.56"")

Summary field mapping:
- ""curr_chg"" = Subtotal (the sum of all line item charges before taxes). Do NOT use Total, Amount Due, or Balance Due for this field
- ""curr_tax"" = total taxes, fees, and surcharges
- ""end_bal"" = Amount Due or Total Due (the final amount the customer must pay)
- ""beg_bal"" = Carried Balance or Previous Balance
- ""payment"" = Amount Paid or Payments Received

Charge extraction rules:
- For charges, extract individual recurring/one-time/renewal line items, NOT summary totals like ""Total"", ""Subtotal"", ""Amount Due"", or ""Balance Due""
- Use the Product/Service name (and plan/edition name if present) as the charge description — e.g. ""Microsoft Exchange Online - Exchange Online (Plan 2)""
- Do NOT use subscription action labels like ""RECURRING CHARGE"" or ""RENEWAL"" as the charge description
- Use the Line Total column as the charge amount (not Unit Price)
- EXCLUDE all $0.00 charges — only include charges where the Line Total is greater than zero
- Do NOT include URLs, payment instructions, legal disclaimers, or tax line items as charges

Other rules:
- For usages, extract Data/Voice/Text/Roaming usage per phone line with limits and amounts
- For inventory, extract one record per phone line with the employee name, plan, and device type
- If usage limit is unlimited, set usage_limit to ""unlimited""
- Phone numbers or circuit IDs go in the ""line"" or ""line_number"" field
- If a field cannot be determined from the text, set it to null
- Return ONLY the JSON object, no markdown, no commentary, no code fences

INVOICE TEXT:
" + pdfText;
    }

    private string ResolveProvider()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Provider) &&
            !_settings.Provider.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return _settings.Provider.ToLowerInvariant();

        if (_settings.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            _settings.BaseUrl.Contains("127.0.0.1") ||
            _settings.BaseUrl.Contains("11434"))
            return "ollama";

        if (_settings.BaseUrl.Contains("appdirect.com", StringComparison.OrdinalIgnoreCase) ||
            _settings.BaseUrl.Contains("devs.ai", StringComparison.OrdinalIgnoreCase))
            return "devs.ai";

        return "openai";
    }

    private async Task<string?> CallApiAsync(string prompt)
    {
        var provider = ResolveProvider();
        var systemMsg = "You are a precise invoice data extraction engine. Always respond with valid JSON only. Never include markdown code fences, commentary, or explanation.";

        object requestBody;

        if (provider == "ollama")
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
                options = new { num_ctx = 32768 }
            };
        }
        else if (provider == "devs.ai")
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
                stream = false
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

        if (provider != "ollama" && !string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            if (provider == "devs.ai")
                request.Headers.TryAddWithoutValidation("X-Authorization", _settings.ApiKey);
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }

        _logger.LogInformation("Sending request to {Url} with model {Model} (provider: {Provider})...",
            url, _settings.Model, provider);

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("AI API returned {StatusCode}: {Error}", (int)response.StatusCode, errorBody);
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("Raw API response: {Response}", responseJson.Length > 2000
            ? responseJson[..2000] + "..." : responseJson);

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                return choices[0].GetProperty("message").GetProperty("content").GetString();
            }

            if (root.TryGetProperty("message", out var message))
            {
                return message.GetProperty("content").GetString();
            }

            _logger.LogWarning("Unexpected API response structure. Keys: {Keys}",
                string.Join(", ", root.EnumerateObject().Select(p => p.Name)));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse API response envelope.");
            return null;
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetRawText();
        return prop.GetString();
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();

        var fencePattern = new Regex(@"^```(?:json)?\s*\n?(.*?)\n?\s*```$", RegexOptions.Singleline);
        var match = fencePattern.Match(trimmed);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        return trimmed;
    }

    private ParsedInvoiceResult? ParseResponse(string responseText)
    {
        try
        {
            var jsonText = StripMarkdownFences(responseText);

            var jsonStart = jsonText.IndexOf('{');
            var jsonEnd = jsonText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
            {
                _logger.LogWarning("No JSON object found in AI response. Content starts with: {Start}",
                    jsonText.Length > 200 ? jsonText[..200] : jsonText);
                return null;
            }
            jsonText = jsonText[jsonStart..(jsonEnd + 1)];

            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            var result = new ParsedInvoiceResult();

            if (root.TryGetProperty("summary", out var summary))
            {
                foreach (var prop in summary.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Null) continue;

                    string? val = null;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        val = prop.Value.GetString();
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                        val = prop.Value.GetRawText();

                    if (!string.IsNullOrWhiteSpace(val) &&
                        !val.Equals("null", StringComparison.OrdinalIgnoreCase))
                        result.SummaryFields[prop.Name] = val;
                }
            }

            if (root.TryGetProperty("charges", out var charges) && charges.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in charges.EnumerateArray())
                {
                    var charge = new ParsedCharge();

                    if (item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null)
                        charge.ChargeDescription = desc.GetString();

                    if (item.TryGetProperty("amount", out var amt))
                    {
                        if (amt.ValueKind == JsonValueKind.Number)
                            charge.Amount = amt.GetDecimal();
                        else if (amt.ValueKind == JsonValueKind.String)
                        {
                            var amtStr = amt.GetString()?.Replace("$", "").Replace(",", "");
                            if (decimal.TryParse(amtStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsedAmt))
                                charge.Amount = parsedAmt;
                        }
                    }

                    if (item.TryGetProperty("line", out var line) && line.ValueKind != JsonValueKind.Null)
                        charge.Line = line.GetString();

                    if (item.TryGetProperty("location", out var loc) && loc.ValueKind != JsonValueKind.Null)
                        charge.Location = loc.GetString();

                    if (!string.IsNullOrWhiteSpace(charge.ChargeDescription))
                        result.Charges.Add(charge);
                }
            }

            if (root.TryGetProperty("usages", out var usages) && usages.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in usages.EnumerateArray())
                {
                    var usage = new ParsedUsageItem
                    {
                        LineNumber = GetJsonString(item, "line_number"),
                        EmployeeName = GetJsonString(item, "employee_name"),
                        UsocName = GetJsonString(item, "description"),
                        UsageLimit = GetJsonString(item, "usage_limit"),
                        UsageAmount = GetJsonString(item, "usage_amount"),
                        Charge = GetJsonString(item, "charge") ?? "0.00",
                        UsageType = GetJsonString(item, "usage_type"),
                    };

                    if (!string.IsNullOrWhiteSpace(usage.LineNumber))
                        result.Usages.Add(usage);
                }
            }

            if (root.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in inventory.EnumerateArray())
                {
                    var inv = new ParsedInventoryItem
                    {
                        ReferenceNumber = GetJsonString(item, "line_number"),
                        EmployeeName = GetJsonString(item, "employee_name"),
                        PlanName = GetJsonString(item, "plan_name"),
                        PlanAmount = GetJsonString(item, "plan_amount"),
                        ServiceType = GetJsonString(item, "service_type"),
                    };

                    if (!string.IsNullOrWhiteSpace(inv.ReferenceNumber))
                        result.Inventories.Add(inv);
                }
            }

            return result.SummaryFields.Count > 0 || result.Charges.Count > 0
                || result.Usages.Count > 0 || result.Inventories.Count > 0
                ? result : null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing failed. Content starts with: {Start}",
                responseText.Length > 300 ? responseText[..300] : responseText);
            return null;
        }
    }
}
