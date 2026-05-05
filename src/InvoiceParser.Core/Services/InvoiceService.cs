using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services.ML;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services;

/// <summary>Optional trace from the Python hybrid LayoutLM + rules microservice (when enabled).</summary>
public sealed class PythonParseTrace
{
    public string? ParseId { get; set; }
    public string? Source { get; set; }
    public string? OcrJson { get; set; }
    public Dictionary<string, float>? Confidence { get; set; }
    public Dictionary<string, string>? PerFieldSource { get; set; }
    /// <summary>Vendor string from Python parser (not mapped to DB summary fields).</summary>
    public string? VendorHint { get; set; }
}

public class ParsePdfResult
{
    public ParsedInvoiceResult Parsed { get; set; } = new();
    public string PdfText { get; set; } = string.Empty;
    public PythonParseTrace? Python { get; set; }
}

public class ChargeFeedback
{
    public string? OriginalDescription { get; set; }
    public string? CorrectedDescription { get; set; }
    /// <summary>"corrected" | "deleted" | "added"</summary>
    public string Action { get; set; } = string.Empty;
}

public class InvoiceService
{
    private static readonly HashSet<string> FeedbackOverlaySummaryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoice_number", "carrier_account", "invoice_date",
        "invoice_st_dtm", "invoice_end_dtm", "invoice_due_dtm",
        "beg_bal", "payment", "prev_adj", "curr_adj",
        "curr_chg", "curr_tax", "end_bal",
    };

    private readonly IInvoiceRepository _repository;
    private readonly PdfTextExtractorService _pdfExtractor;
    private readonly GenericInvoiceParser _parser;
    private readonly VerizonWirelessParser _verizonParser;
    private readonly RuleLearningService _learningService;
    private readonly AiInvoiceParser _aiParser;
    private readonly InvoiceMLService _mlService;
    private readonly FeedbackProcessor _feedbackProcessor;
    private readonly FeedbackAgent _feedbackAgent;
    private readonly ChargeValidationService _chargeValidator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IInvoicePythonAugmenter _pythonAugmenter;

    public InvoiceService(IInvoiceRepository repository, PdfTextExtractorService pdfExtractor,
        GenericInvoiceParser parser, VerizonWirelessParser verizonParser,
        RuleLearningService learningService,
        AiInvoiceParser aiParser, InvoiceMLService mlService,
        FeedbackProcessor feedbackProcessor, FeedbackAgent feedbackAgent,
        ChargeValidationService chargeValidator,
        IServiceScopeFactory scopeFactory,
        IInvoicePythonAugmenter pythonAugmenter,
        ILogger<InvoiceService> logger)
    {
        _repository = repository;
        _pdfExtractor = pdfExtractor;
        _parser = parser;
        _verizonParser = verizonParser;
        _learningService = learningService;
        _aiParser = aiParser;
        _mlService = mlService;
        _feedbackProcessor = feedbackProcessor;
        _feedbackAgent = feedbackAgent;
        _chargeValidator = chargeValidator;
        _scopeFactory = scopeFactory;
        _pythonAugmenter = pythonAugmenter;
        _logger = logger;
    }

    public Task<List<Customer>> GetCustomersAsync() => _repository.GetCustomersAsync();
    public Task<List<Carrier>> GetCarriersAsync() => _repository.GetCarriersAsync();
    public Task<Carrier?> GetCarrierByIdAsync(int id) => _repository.GetCarrierByIdAsync(id);
    public Task<Invoice?> GetInvoiceByIdAsync(int id) => _repository.GetInvoiceByIdAsync(id);
    public Task<List<Invoice>> GetAllInvoicesAsync() => _repository.GetAllInvoicesAsync();

    public async Task SaveUserFeedbackAsync(InvoiceFeedback feedback)
    {
        _logger.LogInformation("=== FEEDBACK PIPELINE START === CarrierId={CarrierId}, Text='{Text}'",
            feedback.CarrierId, feedback.FeedbackText);

        await _repository.SaveFeedbackAsync(feedback);

        var rules = _feedbackProcessor.ProcessFeedback(feedback);
        _logger.LogInformation("FeedbackProcessor generated {Count} rule(s).", rules.Count);

        if (rules.Count == 0 && _feedbackAgent.IsAvailable)
        {
            _logger.LogInformation("No rule-based rules; invoking FeedbackAgent (Ollama)...");
            rules = await _feedbackAgent.ProcessFeedbackAsync(feedback);
            _logger.LogInformation("FeedbackAgent returned {Count} rule(s).", rules.Count);
        }
        else if (rules.Count == 0 && !_feedbackAgent.IsAvailable)
        {
            _logger.LogWarning("No rules generated and FeedbackAgent is NOT available (Ollama not configured/running).");
        }

        if (rules.Count > 0)
        {
            foreach (var r in rules)
                _logger.LogInformation("  Saving rule: Field={Field}, Pattern='{Pattern}', Table={Table}, Type={Type}",
                    r.FieldName, r.RegexPattern, r.TargetTable, r.FieldType);
            await _repository.SaveLearnedRulesAsync(feedback.CarrierId, rules);
            _logger.LogInformation("Saved {Count} learned rule(s) to database.", rules.Count);
        }
        else
        {
            _logger.LogWarning("=== FEEDBACK PIPELINE: No rules generated from feedback. ===");
        }

        await _repository.MarkFeedbackProcessedAsync(feedback.Id);
        _logger.LogInformation("=== FEEDBACK PIPELINE END ===");
    }

    public async Task<List<InvoiceFeedback>> GetUnprocessedFeedbackAsync()
        => await _repository.GetUnprocessedFeedbackAsync();

    public async Task<(int Processed, int RulesGenerated)> ProcessAllUnprocessedFeedbackAsync()
    {
        var feedbackItems = await _repository.GetUnprocessedFeedbackAsync();
        if (feedbackItems.Count == 0)
            return (0, 0);

        _logger.LogInformation("Processing {Count} unprocessed feedback item(s)...", feedbackItems.Count);
        int totalRules = 0;

        foreach (var feedback in feedbackItems)
        {
            try
            {
                var rules = _feedbackProcessor.ProcessFeedback(feedback);

                if (rules.Count == 0 && _feedbackAgent.IsAvailable)
                    rules = await _feedbackAgent.ProcessFeedbackAsync(feedback);

                if (rules.Count > 0)
                {
                    await _repository.SaveLearnedRulesAsync(feedback.CarrierId, rules);
                    totalRules += rules.Count;
                }

                await _repository.MarkFeedbackProcessedAsync(feedback.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process feedback Id={Id}", feedback.Id);
            }
        }

        _logger.LogInformation("Processed {Processed} feedback item(s), generated {Rules} rule(s).",
            feedbackItems.Count, totalRules);

        return (feedbackItems.Count, totalRules);
    }

    public async Task<ParsePdfResult> ParseFileAsync(Stream fileStream, int carrierId, string fileName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== PARSE FILE START === CarrierId={CarrierId}, File={FileName}", carrierId, fileName);

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var fileBytes = buffer.ToArray();

        string text;
        using (var readMs = new MemoryStream(fileBytes))
        {
            if (PdfTextExtractorService.IsImageFile(fileName))
            {
                _logger.LogInformation("Image file detected, using OCR extraction.");
                text = _pdfExtractor.ExtractTextFromImage(readMs);
            }
            else
            {
                text = _pdfExtractor.ExtractText(readMs);
            }
        }

        _logger.LogInformation("Extracted {Length} chars of text.", text.Length);
        var result = await ParseExtractedTextAsync(text, carrierId).ConfigureAwait(false);

        var carrier = await _repository.GetCarrierByIdAsync(carrierId).ConfigureAwait(false);
        await _pythonAugmenter
            .TryAugmentParseAsync(fileBytes, fileName, carrier?.Code, result, cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    public async Task<ParsePdfResult> ParsePdfAsync(Stream pdfStream, int carrierId)
    {
        _logger.LogInformation("=== PARSE PDF START === CarrierId={CarrierId}", carrierId);
        var text = _pdfExtractor.ExtractText(pdfStream);
        _logger.LogInformation("Extracted {Length} chars of PDF text.", text.Length);
        return await ParseExtractedTextAsync(text, carrierId);
    }

    private async Task<ParsePdfResult> ParseExtractedTextAsync(string text, int carrierId)
    {
        var learnedRules = await _repository.GetRulesForCarrierAsync(carrierId);
        _logger.LogInformation("Fetched {Count} learned rules for carrier {CarrierId}.",
            learnedRules.Count, carrierId);

        var carrier = await _repository.GetCarrierByIdAsync(carrierId);
        var parsed = _parser.Parse(text, learnedRules, carrier?.Code);
        string parserUsed = "SmartParse";
        _logger.LogInformation("SmartParse extracted {FieldCount} fields, {ChargeCount} charges.",
            parsed.SummaryFields.Count, parsed.Charges.Count);

        var validSummaryFields = new HashSet<string> {
            "invoice_number", "carrier_account", "invoice_date",
            "invoice_st_dtm", "invoice_end_dtm", "invoice_due_dtm",
            "beg_bal", "payment", "prev_adj", "curr_adj",
            "curr_chg", "curr_tax", "end_bal"
        };

        if (_mlService.IsModelReady)
        {
            var mlResult = _mlService.Predict(text);
            if (mlResult?.SummaryFields.Count > 0)
            {
                int overrideCount = 0;
                foreach (var kvp in mlResult.SummaryFields)
                {
                    if (!validSummaryFields.Contains(kvp.Key)) continue;
                    if (string.IsNullOrWhiteSpace(kvp.Value)) continue;

                    var existingVal = parsed.SummaryFields.GetValueOrDefault(kvp.Key);
                    if (string.IsNullOrWhiteSpace(existingVal))
                    {
                        parsed.SummaryFields[kvp.Key] = kvp.Value;
                        overrideCount++;
                    }
                }
                parserUsed += "+ML";
                _logger.LogInformation("ML model produced {Total} fields, {Valid} filled gaps in SmartParse.",
                    mlResult.SummaryFields.Count, overrideCount);
            }
        }

        if (_aiParser.IsConfigured)
        {
            var aiResult = await _aiParser.ParseAsync(text);
            if (aiResult != null)
            {
                int overrideCount = 0;
                foreach (var kvp in aiResult.SummaryFields)
                {
                    if (!validSummaryFields.Contains(kvp.Key)) continue;
                    if (string.IsNullOrWhiteSpace(kvp.Value)) continue;

                    var existingVal = parsed.SummaryFields.GetValueOrDefault(kvp.Key);
                    if (string.IsNullOrWhiteSpace(existingVal))
                    {
                        parsed.SummaryFields[kvp.Key] = kvp.Value;
                        overrideCount++;
                    }
                }

                if (parsed.Charges.Count == 0 && aiResult.Charges.Count > 0)
                {
                    parsed.Charges.AddRange(aiResult.Charges);
                    _logger.LogInformation("AI filled {Count} charge items (regex found none).", aiResult.Charges.Count);
                }

                if (parsed.Usages.Count == 0 && aiResult.Usages.Count > 0)
                {
                    parsed.Usages.AddRange(aiResult.Usages);
                    _logger.LogInformation("AI filled {Count} usage items (regex found none).", aiResult.Usages.Count);
                }

                if (parsed.Inventories.Count == 0 && aiResult.Inventories.Count > 0)
                {
                    parsed.Inventories.AddRange(aiResult.Inventories);
                    _logger.LogInformation("AI filled {Count} inventory items (regex found none).", aiResult.Inventories.Count);
                }

                parserUsed += "+AI";
                _logger.LogInformation("AI parser produced {Total} fields, {Valid} filled gaps.",
                    aiResult.SummaryFields.Count, overrideCount);
            }
        }

        _logger.LogInformation("Parser used: {Parser}. Fields extracted: {Fields}",
            parserUsed,
            string.Join(", ", parsed.SummaryFields.Select(f => $"{f.Key}={f.Value ?? "(null)"}")));

        if (_verizonParser.IsVerizonWirelessFormat(text))
        {
            _verizonParser.Parse(text, parsed);
            _logger.LogInformation("VerizonWirelessParser extracted {UsageCount} usage items, {InventoryCount} inventory items.",
                parsed.Usages.Count, parsed.Inventories.Count);

            ApplyLearnedChargeSkipRules(parsed, learnedRules);
        }

        var learnedSkipPatterns = learnedRules
            .Where(r => r.TargetTable == "t_charge" && r.FieldType == "skip" && r.IsActive)
            .Select(r => r.RegexPattern)
            .ToList();

        var validationRemoved = await _chargeValidator.ValidateAndFilterAsync(parsed, learnedSkipPatterns);
        if (validationRemoved > 0)
            _logger.LogInformation("ChargeValidation removed {Count} invalid charge(s) total.", validationRemoved);

        // Extraction complete → synthetic invoice # if needed → apply stored user corrections (overrides).
        GenerateFallbackInvoiceNumber(parsed);

        var acctKey = FeedbackFieldNormalizer.NormalizeAccountKey(
            parsed.SummaryFields.GetValueOrDefault("carrier_account"));
        var dateRaw = parsed.SummaryFields.GetValueOrDefault("invoice_date");
        if (!string.IsNullOrEmpty(acctKey) &&
            FeedbackFieldNormalizer.TryNormalizeInvoiceDateKey(dateRaw, out var dateKey))
        {
            var priorFeedback = await _repository
                .GetLatestFeedbackByCarrierAccountAndInvoiceDateAsync(carrierId, acctKey, dateKey)
                .ConfigureAwait(false);
            if (priorFeedback != null)
            {
                ApplyConfirmedFeedbackOverlay(parsed, priorFeedback);
                _logger.LogInformation(
                    "Applied confirmed feedback overlay for CarrierId={CarrierId}, account key={Acct}, date={Date} (FeedbackId={Fid}).",
                    carrierId, acctKey, dateKey, priorFeedback.Id);
            }
        }

        _logger.LogInformation("=== PARSE COMPLETE === invoice_number={InvNum}, {ChargeCount} charges, {UsageCount} usages, {InventoryCount} inventories",
            parsed.SummaryFields.GetValueOrDefault("invoice_number", "(none)"),
            parsed.Charges.Count, parsed.Usages.Count, parsed.Inventories.Count);

        return new ParsePdfResult
        {
            Parsed = parsed,
            PdfText = text,
        };
    }

    private void ApplyLearnedChargeSkipRules(ParsedInvoiceResult parsed,
        List<VendorParsingRule> learnedRules)
    {
        var skipPatterns = learnedRules
            .Where(r => r.TargetTable == "t_charge" && r.FieldType == "skip" && r.IsActive)
            .Select(r => r.RegexPattern)
            .ToList();

        if (skipPatterns.Count == 0) return;

        int before = parsed.Charges.Count;
        parsed.Charges.RemoveAll(c =>
        {
            if (string.IsNullOrWhiteSpace(c.ChargeDescription)) return false;
            return skipPatterns.Any(p =>
            {
                try { return Regex.IsMatch(c.ChargeDescription, p, RegexOptions.IgnoreCase); }
                catch { return false; }
            });
        });

        int removed = before - parsed.Charges.Count;
        if (removed > 0)
            _logger.LogInformation("Learned skip rules removed {Count} charge(s) from Verizon results.", removed);
    }

    /// <summary>
    /// When no invoice number is found, generate one from sanitized AccountNumber + "X" + InvoiceDate (MMddyyyy).
    /// </summary>
    private static void GenerateFallbackInvoiceNumber(ParsedInvoiceResult parsed)
    {
        parsed.SummaryFields.TryGetValue("invoice_number", out var invNum);
        if (!string.IsNullOrWhiteSpace(invNum))
            return;

        parsed.SummaryFields.TryGetValue("carrier_account", out var accountRaw);
        parsed.SummaryFields.TryGetValue("invoice_date", out var dateStr);

        var account = FeedbackFieldNormalizer.NormalizeAccountKey(accountRaw);
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(dateStr))
            return;

        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            parsed.SummaryFields["invoice_number"] = $"{account}X{dt:MMddyyyy}";
    }

    /// <summary>Merge prior user-confirmed summary fields; wins over extraction/ML/AI.</summary>
    private static void ApplyConfirmedFeedbackOverlay(ParsedInvoiceResult parsed, InvoiceFeedback feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback.ConfirmedFieldsJson))
            return;

        Dictionary<string, string?>? confirmed;
        try
        {
            confirmed = JsonSerializer.Deserialize<Dictionary<string, string?>>(feedback.ConfirmedFieldsJson);
        }
        catch
        {
            return;
        }

        if (confirmed == null) return;

        foreach (var kv in confirmed)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (!FeedbackOverlaySummaryKeys.Contains(kv.Key)) continue;
            parsed.SummaryFields[kv.Key] = kv.Value;
        }
    }

    public async Task<TrainingResult> TrainModelAsync()
    {
        var invoices = await _repository.GetInvoicesWithPdfTextAsync();
        var feedback = await _repository.GetUnprocessedFeedbackAsync();
        var result = _mlService.Train(invoices, feedback);

        if (result.Success && feedback.Count > 0)
        {
            var ids = feedback.Select(f => f.Id).ToList();
            await _repository.MarkFeedbackBatchProcessedAsync(ids);
        }

        return result;
    }

    public async Task<Invoice?> FindDuplicateInvoiceAsync(string? invoiceNumber, int? carrierId)
        => await _repository.FindDuplicateInvoiceAsync(invoiceNumber, carrierId);

    public async Task<Invoice> SaveParsedInvoiceAsync(Invoice invoice, List<Charge> charges)
    {
        foreach (var charge in charges)
        {
            charge.CarrierId = invoice.CarrierId;
            charge.AccountNumber = invoice.CarrierAccount;
            invoice.Charges.Add(charge);
        }

        return await _repository.SaveInvoiceAsync(invoice);
    }

    public async Task<Invoice> SaveAllAsync(Invoice invoice, List<Charge> charges,
        List<Usage> usages, List<Inventory> inventories)
    {
        foreach (var charge in charges)
        {
            charge.CarrierId = invoice.CarrierId;
            charge.AccountNumber = invoice.CarrierAccount;
            invoice.Charges.Add(charge);
        }

        await _repository.SaveInvoiceWithRelatedDataAsync(invoice, usages, inventories);
        return invoice;
    }

    public async Task SaveUsagesAsync(int invoiceId, int? carrierId, List<ParsedUsageItem> items)
    {
        var entities = items.Select(u => new Usage
        {
            InvoiceId = invoiceId,
            LineNumber = u.LineNumber,
            UsocName = u.UsocName,
            UsageLimit = u.UsageLimit,
            UsageAmount = u.UsageAmount,
            Charge = u.Charge,
            UsageType = u.UsageType,
            CarrierId = carrierId,
            AddDate = DateTime.Now,
        }).ToList();

        await _repository.SaveUsagesAsync(entities);
    }

    public async Task SaveInventoriesAsync(int invoiceId, int customerId, List<ParsedInventoryItem> items)
    {
        var entities = items
            .Where(inv => !string.IsNullOrWhiteSpace(inv.ReferenceNumber))
            .Select(inv =>
            {
                var planAmt = inv.PlanAmount;
                if (!string.IsNullOrWhiteSpace(inv.EmployeeName)
                    && (string.IsNullOrWhiteSpace(planAmt) || !decimal.TryParse(planAmt, out _)))
                {
                    planAmt = "0.00";
                }

                return new Inventory
                {
                    InvoiceId = invoiceId,
                    CustomerId = customerId,
                    ReferenceNumber = inv.ReferenceNumber!,
                    EmployeeName = inv.EmployeeName,
                    InventoryName = inv.PlanName,
                    ServiceType = inv.ServiceType,
                    LocationName = planAmt,
                };
            }).ToList();

        await _repository.SaveInventoriesAsync(entities);
    }

    public async Task LearnFromInvoiceAsync(string pdfText,
        Dictionary<string, string?> confirmedFields, int carrierId)
    {
        var rules = _learningService.GenerateRules(pdfText, confirmedFields, carrierId);
        if (rules.Count > 0)
            await _repository.SaveLearnedRulesAsync(carrierId, rules);
    }

    public async Task ProcessFeedbackAndLearnAsync(
        string pdfText, int carrierId,
        Dictionary<string, string?> confirmedFields,
        Dictionary<string, string?> originalFields,
        List<ChargeFeedback>? chargeFeedback = null)
    {
        var correctFields = new List<string>();
        var correctedFields = new List<string>();

        foreach (var (key, confirmedVal) in confirmedFields)
        {
            originalFields.TryGetValue(key, out var origVal);
            var origNorm = NormalizeForCompare(origVal);
            var confNorm = NormalizeForCompare(confirmedVal);

            if (string.IsNullOrEmpty(origNorm) && string.IsNullOrEmpty(confNorm))
                continue;

            if (origNorm == confNorm)
                correctFields.Add(key);
            else
                correctedFields.Add(key);
        }

        if (correctFields.Count > 0 || correctedFields.Count > 0)
            await _repository.UpdateRuleFeedbackAsync(carrierId, correctFields, correctedFields);

        await LearnFromInvoiceAsync(pdfText, confirmedFields, carrierId);

        // Process charge-level feedback: learn skip patterns from corrected descriptions
        if (chargeFeedback != null)
            await LearnFromChargeFeedbackAsync(carrierId, chargeFeedback);

        await TryAutoRetrainAsync();
    }

    private async Task LearnFromChargeFeedbackAsync(int carrierId, List<ChargeFeedback> feedback)
    {
        var learnedRules = new List<VendorParsingRule>();

        foreach (var item in feedback)
        {
            if (item.Action == "corrected" &&
                !string.IsNullOrWhiteSpace(item.OriginalDescription) &&
                !string.IsNullOrWhiteSpace(item.CorrectedDescription))
            {
                var origTrimmed = item.OriginalDescription.Trim();
                var corrTrimmed = item.CorrectedDescription.Trim();

                // The user shortened the description — the removed portion is noise
                if (origTrimmed.StartsWith(corrTrimmed, StringComparison.OrdinalIgnoreCase) &&
                    origTrimmed.Length > corrTrimmed.Length)
                {
                    var removedSuffix = origTrimmed[corrTrimmed.Length..].Trim();
                    if (removedSuffix.Length >= 3)
                    {
                        learnedRules.Add(BuildChargeSkipRule(carrierId, removedSuffix));
                    }
                }
                else if (origTrimmed.EndsWith(corrTrimmed, StringComparison.OrdinalIgnoreCase) &&
                         origTrimmed.Length > corrTrimmed.Length)
                {
                    var removedPrefix = origTrimmed[..^corrTrimmed.Length].Trim();
                    if (removedPrefix.Length >= 3)
                    {
                        learnedRules.Add(BuildChargeSkipRule(carrierId, removedPrefix));
                    }
                }
                else if (origTrimmed.Contains(corrTrimmed, StringComparison.OrdinalIgnoreCase) &&
                         origTrimmed.Length > corrTrimmed.Length)
                {
                    var idx = origTrimmed.IndexOf(corrTrimmed, StringComparison.OrdinalIgnoreCase);
                    var before = origTrimmed[..idx].Trim();
                    var after = origTrimmed[(idx + corrTrimmed.Length)..].Trim();
                    if (before.Length >= 3) learnedRules.Add(BuildChargeSkipRule(carrierId, before));
                    if (after.Length >= 3) learnedRules.Add(BuildChargeSkipRule(carrierId, after));
                }
            }
            else if (item.Action == "deleted" && !string.IsNullOrWhiteSpace(item.OriginalDescription))
            {
                // Entire charge was wrong — learn to skip this description pattern
                learnedRules.Add(BuildChargeSkipRule(carrierId, item.OriginalDescription.Trim()));
            }
        }

        if (learnedRules.Count > 0)
            await _repository.SaveLearnedRulesAsync(carrierId, learnedRules);
    }

    private static VendorParsingRule BuildChargeSkipRule(int carrierId, string noiseText)
    {
        var escaped = Regex.Escape(noiseText);
        return new VendorParsingRule
        {
            CarrierId = carrierId,
            FieldName = "charge_skip_pattern",
            RegexPattern = escaped,
            FieldType = "skip",
            TargetTable = "t_charge",
            SortOrder = 100,
            IsActive = true,
            SuccessCount = 1,
        };
    }

    private async Task TryAutoRetrainAsync()
    {
        const int retrainThreshold = 5;
        var invoiceCount = await _repository.GetInvoiceCountWithPdfTextAsync();
        var lastTrained = _mlService.LastTrainedInvoiceCount;

        if (invoiceCount < InvoiceMLService.MinimumTrainingInvoices ||
            invoiceCount - lastTrained < retrainThreshold)
            return;

        _logger.LogInformation("Auto-retrain threshold met ({Count} invoices, last trained at {Last}). Queuing background training.",
            invoiceCount, lastTrained);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();

                var invoices = await repo.GetInvoicesWithPdfTextAsync();
                var feedback = await repo.GetUnprocessedFeedbackAsync();
                var result = _mlService.Train(invoices, feedback);

                if (result.Success && feedback.Count > 0)
                {
                    var ids = feedback.Select(f => f.Id).ToList();
                    await repo.MarkFeedbackBatchProcessedAsync(ids);
                }

                _logger.LogInformation("Background training completed: {Message}", result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background ML training failed.");
            }
        });
    }

    private static string NormalizeForCompare(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("$", "").Replace(",", "").Replace(" ", "").Trim().ToLowerInvariant();
    }
}
