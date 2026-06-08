using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services;
using InvoiceParser.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InvoiceParser.Web.Controllers;

public class CarrierRulesController : Controller
{
    private readonly IInvoiceRepository _repo;
    private readonly ICarrierRuleStore _ruleStore;
    private readonly GenericInvoiceParser _parser;
    private readonly PdfTextExtractorService _pdfExtractor;
    private readonly ILogger<CarrierRulesController> _logger;

    private static readonly string[] KnownSummaryFields =
    {
        "invoice_number", "carrier_account", "invoice_date", "invoice_st_dtm",
        "invoice_end_dtm", "invoice_due_dtm", "beg_bal", "payment",
        "prev_adj", "curr_adj", "curr_chg", "curr_tax", "end_bal",
    };

    private static readonly string[] KnownChargeFields =
    {
        "line_number", "location", "charge_desc", "amount",
        "section_start", "section_end",
    };

    private static readonly string[] KnownUsageFields =
    {
        "line_number", "usoc_name", "usage_limit", "usage", "charge", "usagetype",
    };

    private static readonly string[] KnownInventoryFields =
    {
        "reference_number", "service_type", "inventory_name", "employee_name", "location_name",
    };

    /// <summary>
    /// Human-readable field labels mapped to internal extraction metadata.
    /// Grouped by target table so the wizard can filter to only show relevant fields.
    /// </summary>
    public static readonly Dictionary<string, WizardFieldDef> FriendlyFields = new()
    {
        // ── Invoice Summary (t_invoice) ──────────────────────────────────────
        ["Invoice Number"]      = new("invoice_number",  "t_invoice", "string",          "The unique invoice identifier printed on the bill."),
        ["Account Number"]      = new("carrier_account", "t_invoice", "string",          "The carrier account number for this customer."),
        ["Invoice Date"]        = new("invoice_date",     "t_invoice", "date",            "The date the invoice was issued / bill date."),
        ["Due Date"]            = new("invoice_due_dtm",  "t_invoice", "date",            "The payment due date."),
        ["Statement Start"]     = new("invoice_st_dtm",   "t_invoice", "date",            "First day of the billing period."),
        ["Statement End"]       = new("invoice_end_dtm",  "t_invoice", "date",            "Last day of the billing period."),
        ["Previous Balance"]    = new("beg_bal",          "t_invoice", "decimal",         "Balance carried forward from the previous invoice."),
        ["Payments"]            = new("payment",          "t_invoice", "decimal",         "Payments received during this billing period."),
        ["Current Charges"]     = new("curr_chg",         "t_invoice", "decimal",         "New charges for this billing period."),
        ["Taxes & Fees"]        = new("curr_tax",         "t_invoice", "decimal",         "Government taxes, surcharges, and fees."),
        ["Total Charges"]       = new("end_bal",          "t_invoice", "decimal",         "Total charges — the ending balance / amount due on this invoice."),

        // ── Line Item Charges (t_charge) ─────────────────────────────────────
        ["Charge Description"]  = new("charge_desc",      "t_charge",  "string",          "Description of the charge (e.g. 'Monthly Service Fee', 'Long Distance')."),
        ["Charge Amount"]       = new("amount",            "t_charge",  "decimal",         "The dollar amount for this charge line."),
        ["Line Number"]         = new("line_number",       "t_charge",  "line_anchor",     "Sets the phone/circuit number context for the charges that follow (e.g. 'FOR 061-827-9465')."),
        ["Location"]            = new("location",          "t_charge",  "location_anchor", "Sets the service address context for charges that follow (e.g. 'Service at: BEND, OR')."),
        ["Section Start"]       = new("section_start",     "t_charge",  "section_start",   "When a line matches this condition, begin collecting charge rows below it (e.g. 'Charges For', 'Current Activity')."),
        ["Section End"]         = new("section_end",       "t_charge",  "section_end",     "When a line matches this condition, stop collecting charges (e.g. 'Subtotal', 'Total Due', 'BALANCE DUE')."),
        ["Skip Charge Line"]    = new("skip",              "t_charge",  "skip",            "Lines matching this rule will be completely ignored during charge extraction."),

        // ── Usage Records (t_usage) ──────────────────────────────────────────
        ["Usage – Line Number"] = new("line_number",       "t_usage",   "string",          "The phone/circuit number associated with this usage record."),
        ["Usage – Service"]     = new("usoc_name",         "t_usage",   "string",          "Service or feature name (USOC description, e.g. 'Voice', 'Data Plan')."),
        ["Usage – Limit"]       = new("usage_limit",       "t_usage",   "string",          "Included allowance or cap (e.g. 'Unlimited', '2 GB', '500 min')."),
        ["Usage – Amount Used"] = new("usage",             "t_usage",   "string",          "Actual usage quantity (e.g. '1.5 GB', '450 min')."),
        ["Usage – Charge"]      = new("charge",            "t_usage",   "decimal",         "Dollar amount billed for this usage item."),
        ["Usage – Type"]        = new("usagetype",         "t_usage",   "string",          "Category of usage (e.g. 'Voice', 'Data', 'SMS', 'Roaming')."),
        ["Skip Usage Line"]     = new("skip",              "t_usage",   "skip",            "Usage rows matching this rule will be ignored during extraction."),

        // ── Inventory (t_inventory) ───────────────────────────────────────────
        ["Inventory – Reference #"] = new("reference_number", "t_inventory", "string", "Unique reference or service order number for this item."),
        ["Inventory – Service Type"]= new("service_type",     "t_inventory", "string", "Type of service (e.g. 'Voice Line', 'Data Plan', 'Equipment')."),
        ["Inventory – Name"]        = new("inventory_name",   "t_inventory", "string", "Name of the inventory item or feature."),
        ["Inventory – Employee"]    = new("employee_name",    "t_inventory", "string", "Employee or user assigned to this inventory item."),
        ["Inventory – Location"]    = new("location_name",    "t_inventory", "string", "Physical location or site for this inventory item."),
        ["Skip Inventory Line"]     = new("skip",             "t_inventory", "skip",   "Inventory rows matching this rule will be ignored during extraction."),
    };

    public CarrierRulesController(IInvoiceRepository repo,
        ICarrierRuleStore ruleStore,
        GenericInvoiceParser parser,
        PdfTextExtractorService pdfExtractor,
        ILogger<CarrierRulesController> logger)
    {
        _repo = repo;
        _ruleStore = ruleStore;
        _parser = parser;
        _pdfExtractor = pdfExtractor;
        _logger = logger;
    }

    // GET /CarrierRules/Index/{carrierId}
    public async Task<IActionResult> Index(int carrierId)
    {
        var carrier = await _repo.GetCarrierByIdAsync(carrierId);
        if (carrier == null) return NotFound();

        var rules = await _ruleStore.GetAllRulesForCarrierAsync(carrierId);
        ViewBag.Carrier = carrier;
        ViewBag.CarrierId = carrierId;
        return View(rules);
    }

    // GET /CarrierRules/Carriers — list all carriers to pick one
    public async Task<IActionResult> Carriers()
    {
        var carriers = await _repo.GetCarriersAsync();
        return View(carriers);
    }

    // GET /CarrierRules/Create/{carrierId}
    public async Task<IActionResult> Create(int carrierId)
    {
        var carrier = await _repo.GetCarrierByIdAsync(carrierId);
        if (carrier == null) return NotFound();

        var vm = new RuleEditViewModel
        {
            CarrierId = carrierId,
            CarrierName = carrier.Name,
            TargetTable = "t_invoice",
            ConditionType = "regex_match",
            ConditionSource = "line_text",
            FieldType = "string",
            IsActive = true,
            SortOrder = 10,
        };
        PopulateViewBag(carrierId, carrier.Name ?? string.Empty);
        return View("Edit", vm);
    }

    // GET /CarrierRules/Wizard/{carrierId}[&section=t_invoice|t_charge|t_usage|t_inventory]
    public async Task<IActionResult> Wizard(int carrierId, string? section = null)
    {
        var carrier = await _repo.GetCarrierByIdAsync(carrierId);
        if (carrier == null) return NotFound();

        var pdfText = await _repo.GetLatestPdfTextForCarrierAsync(carrierId);
        var lines = pdfText?
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 1)
            .ToList() ?? new List<string>();

        var sectionLabel = section switch
        {
            "t_invoice"   => "Invoice Summary",
            "t_charge"    => "Line Item Charges",
            "t_usage"     => "Usage Records",
            "t_inventory" => "Inventory",
            _             => null,
        };

        ViewBag.CarrierId  = carrierId;
        ViewBag.CarrierName = carrier.Name;
        ViewBag.Section = section;
        ViewBag.SectionLabel = sectionLabel;
        ViewBag.PdfLinesJson = JsonSerializer.Serialize(lines);
        ViewBag.FriendlyFieldsJson = JsonSerializer.Serialize(
            FriendlyFields.Select(kv => new
            {
                label       = kv.Key,
                fieldName   = kv.Value.FieldName,
                targetTable = kv.Value.TargetTable,
                fieldType   = kv.Value.FieldType,
                description = kv.Value.Description,
            }));
        return View();
    }

    // GET /CarrierRules/Edit/{id}
    public async Task<IActionResult> Edit(int id)
    {
        var rule = await _ruleStore.GetRuleByIdAsync(id);
        if (rule == null) return NotFound();

        var carrier = await _repo.GetCarrierByIdAsync(rule.CarrierId);
        var vm = MapToViewModel(rule, carrier?.Name);
        PopulateViewBag(rule.CarrierId, carrier?.Name ?? string.Empty);
        return View(vm);
    }

    // POST /CarrierRules/Save
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(RuleEditViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            var carrier = await _repo.GetCarrierByIdAsync(vm.CarrierId);
            PopulateViewBag(vm.CarrierId, carrier?.Name ?? string.Empty);
            return View("Edit", vm);
        }

        // Validate regex when ConditionType is regex_match
        if (IsRegexCondition(vm.ConditionType) && !string.IsNullOrWhiteSpace(vm.RegexPattern))
        {
            try { _ = new Regex(vm.RegexPattern); }
            catch
            {
                ModelState.AddModelError(nameof(vm.RegexPattern), "Invalid regular expression pattern.");
                var c = await _repo.GetCarrierByIdAsync(vm.CarrierId);
                PopulateViewBag(vm.CarrierId, c?.Name ?? string.Empty);
                return View("Edit", vm);
            }
        }

        var rule = vm.Id > 0
            ? (await _ruleStore.GetRuleByIdAsync(vm.Id))!
            : new VendorParsingRule { CarrierId = vm.CarrierId };

        if (rule == null) return NotFound();

        MapFromViewModel(vm, rule);

        if (vm.Id > 0)
            await _ruleStore.UpdateRuleAsync(rule);
        else
            await _ruleStore.AddRuleAsync(rule);

        TempData["SuccessMessage"] = $"Rule '{rule.FieldName}' saved.";
        return RedirectToAction("Index", new { carrierId = rule.CarrierId });
    }

    // POST /CarrierRules/Delete/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var rule = await _ruleStore.GetRuleByIdAsync(id);
        if (rule == null) return NotFound();
        var carrierId = rule.CarrierId;
        await _ruleStore.DeleteRuleAsync(id);
        TempData["SuccessMessage"] = "Rule deleted.";
        return RedirectToAction("Index", new { carrierId });
    }

    // POST /CarrierRules/Toggle/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var rule = await _ruleStore.GetRuleByIdAsync(id);
        if (rule == null) return NotFound();
        rule.IsActive = !rule.IsActive;
        await _ruleStore.UpdateRuleAsync(rule);
        return RedirectToAction("Index", new { carrierId = rule.CarrierId });
    }

    // POST /CarrierRules/Test — JSON endpoint used by the rule editor's "Test Pattern" button
    [HttpPost]
    public async Task<IActionResult> Test([FromBody] RuleTestRequest request)
    {
        if (request == null) return BadRequest();

        try
        {
            string pdfText;
            if (!string.IsNullOrWhiteSpace(request.PdfText))
            {
                pdfText = request.PdfText;
            }
            else
            {
                pdfText = await _repo.GetLatestPdfTextForCarrierAsync(request.CarrierId) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(pdfText))
                return Ok(new { success = false, message = "No invoice text available to test against." });

            var results = new List<string>();

            var lines = pdfText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            int matchCount = 0;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                string? extracted = null;

                if (IsRegexCondition(request.ConditionType))
                {
                    if (string.IsNullOrWhiteSpace(request.RegexPattern)) continue;
                    try
                    {
                        var m = Regex.Match(line, request.RegexPattern, RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        extracted = m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim();
                    }
                    catch { continue; }
                }
                else
                {
                    if (!ConditionEvaluator.Evaluate(line, request.ConditionType ?? "regex_match", request.RegexPattern))
                        continue;
                    extracted = line;
                }

                // Apply transforms — pass pdfText + matched line for join_next/prev_line support
                if (!string.IsNullOrWhiteSpace(request.TransformationsJson))
                {
                    var steps = TransformPipeline.Deserialize(request.TransformationsJson);
                    if (steps != null && extracted != null)
                        extracted = TransformPipeline.Apply(extracted, steps,
                            pdfText: pdfText, matchedLine: line);
                }

                if (extracted != null)
                {
                    results.Add(extracted);
                    matchCount++;
                    if (matchCount >= 10) break;
                }
            }

            if (results.Count == 0)
                return Ok(new { success = false, message = "No matches found in the invoice text." });

            return Ok(new { success = true, matches = results, count = results.Count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rule test failed");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    // POST /CarrierRules/QuickCreate — called from the Review page "Create Rule" modal
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QuickCreate(RuleEditViewModel vm)
    {
        var carrier = await _repo.GetCarrierByIdAsync(vm.CarrierId);
        if (carrier == null)
            return Json(new { success = false, message = "Carrier not found." });

        var rule = new VendorParsingRule { CarrierId = vm.CarrierId };
        MapFromViewModel(vm, rule);
        rule.IsActive = true;

        await _ruleStore.AddRuleAsync(rule);

        return Json(new { success = true, message = $"Rule for '{rule.FieldName}' saved." });
    }

    // GET /CarrierRules/PdfLines/{carrierId} — returns lines of the latest invoice text for a carrier
    [HttpGet]
    public async Task<IActionResult> PdfLines(int carrierId)
    {
        var pdfText = await _repo.GetLatestPdfTextForCarrierAsync(carrierId);
        if (string.IsNullOrWhiteSpace(pdfText))
            return Ok(new { lines = Array.Empty<string>() });

        var lines = pdfText
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        return Ok(new { lines });
    }

    // POST /CarrierRules/ExtractLines — accepts a PDF upload, returns its text lines (for wizard)
    [HttpPost]
    public async Task<IActionResult> ExtractLines(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".pdf" && !PdfTextExtractorService.IsImageFile(file.FileName))
            return BadRequest(new { error = "Only PDF and image files are supported." });

        try
        {
            string text;
            await using var stream = file.OpenReadStream();
            text = ext == ".pdf"
                ? _pdfExtractor.ExtractText(stream)
                : _pdfExtractor.ExtractTextFromImage(stream);

            if (string.IsNullOrWhiteSpace(text))
                return Ok(new { lines = Array.Empty<string>(), warning = "No text could be extracted from this file. It may be a scanned image PDF — try uploading it through the main Invoice Upload page which uses OCR." });

            var lines = text
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 1)
                .ToArray();

            return Ok(new { lines, count = lines.Length });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExtractLines failed for file {FileName}", file.FileName);
            return Ok(new { lines = Array.Empty<string>(), warning = $"Could not extract text: {ex.Message}" });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsRegexCondition(string? ct)
        => string.IsNullOrEmpty(ct) || ct.Equals("regex_match", StringComparison.OrdinalIgnoreCase);

    private static RuleEditViewModel MapToViewModel(VendorParsingRule rule, string? carrierName)
        => new()
        {
            Id = rule.Id,
            CarrierId = rule.CarrierId,
            CarrierName = carrierName,
            FieldName = rule.FieldName,
            RegexPattern = rule.RegexPattern,
            FieldType = rule.FieldType,
            TargetTable = rule.TargetTable,
            Section = rule.Section,
            SortOrder = rule.SortOrder,
            IsActive = rule.IsActive,
            ConditionType = rule.ConditionType,
            ConditionSource = rule.ConditionSource,
            TransformationsJson = rule.TransformationsJson,
        };

    private static void MapFromViewModel(RuleEditViewModel vm, VendorParsingRule rule)
    {
        rule.FieldName = vm.FieldName?.Trim() ?? string.Empty;
        rule.RegexPattern = vm.RegexPattern?.Trim() ?? string.Empty;
        rule.FieldType = vm.FieldType ?? "string";
        rule.TargetTable = vm.TargetTable ?? "t_invoice";
        rule.Section = string.IsNullOrWhiteSpace(vm.Section) ? null : vm.Section.Trim();
        rule.SortOrder = vm.SortOrder;
        rule.IsActive = vm.IsActive;
        rule.ConditionType = vm.ConditionType ?? "regex_match";
        rule.ConditionSource = vm.ConditionSource ?? "line_text";
        rule.TransformationsJson = string.IsNullOrWhiteSpace(vm.TransformationsJson)
            ? null : vm.TransformationsJson.Trim();
    }

    private void PopulateViewBag(int carrierId, string carrierName)
    {
        ViewBag.CarrierId = carrierId;
        ViewBag.CarrierName = carrierName;
        ViewBag.ConditionTypes = new SelectList(new[]
        {
            new { Value = "regex_match",       Text = "Regex Match" },
            new { Value = "equals",            Text = "Equals" },
            new { Value = "not_equals",        Text = "Not Equals" },
            new { Value = "contains",          Text = "Contains" },
            new { Value = "does_not_contain",  Text = "Does Not Contain" },
            new { Value = "starts_with",       Text = "Starts With" },
            new { Value = "ends_with",         Text = "Ends With" },
            new { Value = "is_empty",          Text = "Is Empty" },
            new { Value = "is_not_empty",      Text = "Is Not Empty" },
            new { Value = "greater_than",      Text = "Greater Than" },
            new { Value = "less_than",         Text = "Less Than" },
        }, "Value", "Text");

        ViewBag.ConditionSources = new SelectList(new[]
        {
            new { Value = "line_text",    Text = "Line Text" },
            new { Value = "amount",       Text = "Amount Value" },
            new { Value = "field_value",  Text = "Already Extracted Field Value" },
        }, "Value", "Text");

        ViewBag.TargetTables = new SelectList(new[]
        {
            new { Value = "t_invoice",   Text = "Invoice Summary (t_invoice)" },
            new { Value = "t_charge",    Text = "Line Item / Charge (t_charge)" },
            new { Value = "t_usage",     Text = "Usage Records (t_usage)" },
            new { Value = "t_inventory", Text = "Inventory (t_inventory)" },
        }, "Value", "Text");

        ViewBag.FieldTypes = new SelectList(new[]
        {
            new { Value = "string",          Text = "String" },
            new { Value = "date",            Text = "Date" },
            new { Value = "decimal",         Text = "Decimal / Amount" },
            new { Value = "skip",            Text = "Skip (ignore matching lines)" },
            new { Value = "line_anchor",     Text = "Line Anchor (sets Line Number context)" },
            new { Value = "location_anchor", Text = "Location Anchor (sets Location context)" },
            new { Value = "section_start",   Text = "Section Start (begin collecting charges here)" },
            new { Value = "section_end",     Text = "Section End (stop collecting charges here)" },
        }, "Value", "Text");

        ViewBag.KnownFields = KnownSummaryFields
            .Concat(KnownChargeFields)
            .Concat(KnownUsageFields)
            .Concat(KnownInventoryFields)
            .Distinct()
            .Select(f => new SelectListItem(f, f))
            .ToList();

        // Pre-serialize to JSON so Razor can inline it directly without dynamic binding
        ViewBag.TransformTypesJson = JsonSerializer.Serialize(new[]
        {
            new { value = "remove_prefix",   label = "Remove Prefix",                    hasV1 = true,  hasV2 = false, v1label = "Prefix to remove" },
            new { value = "remove_suffix",   label = "Remove Suffix",                    hasV1 = true,  hasV2 = false, v1label = "Suffix to remove" },
            new { value = "replace",         label = "Replace Text",                     hasV1 = true,  hasV2 = true,  v1label = "Find text" },
            new { value = "regex_remove",    label = "Remove Pattern (Regex)",           hasV1 = true,  hasV2 = false, v1label = "Regex pattern to erase (e.g. \\s*\\d{1,2}/\\d{1,2}/\\d{4})" },
            new { value = "trim",            label = "Trim Whitespace",                  hasV1 = false, hasV2 = false, v1label = "" },
            new { value = "extract_between", label = "Extract Between",                  hasV1 = true,  hasV2 = true,  v1label = "Start marker" },
            new { value = "regex_extract",   label = "Regex Extract (Capture Group 1)", hasV1 = true,  hasV2 = false, v1label = "Regex pattern" },
            new { value = "convert_date",    label = "Convert Date Format",              hasV1 = true,  hasV2 = true,  v1label = "Input format (e.g. MM/dd/yy)" },
            new { value = "to_upper",        label = "To Upper Case",                    hasV1 = false, hasV2 = false, v1label = "" },
            new { value = "to_lower",        label = "To Lower Case",                    hasV1 = false, hasV2 = false, v1label = "" },
            new { value = "join_next_line",  label = "Join Next Line",                   hasV1 = true,  hasV2 = false, v1label = "Separator (default: space)" },
            new { value = "join_prev_line",  label = "Join Previous Line",               hasV1 = true,  hasV2 = false, v1label = "Separator (default: space)" },
        });
    }
}

// ── View Models ───────────────────────────────────────────────────────────────

public class RuleEditViewModel
{
    public int Id { get; set; }
    public int CarrierId { get; set; }
    public string? CarrierName { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string RegexPattern { get; set; } = string.Empty;
    public string FieldType { get; set; } = "string";
    public string TargetTable { get; set; } = "t_invoice";
    public string? Section { get; set; }
    public int SortOrder { get; set; } = 10;
    public bool IsActive { get; set; } = true;
    public string ConditionType { get; set; } = "regex_match";
    public string ConditionSource { get; set; } = "line_text";
    public string? TransformationsJson { get; set; }
}

public record WizardFieldDef(string FieldName, string TargetTable, string FieldType, string Description);

public class RuleTestRequest
{
    public int CarrierId { get; set; }
    public string? PdfText { get; set; }
    public string? ConditionType { get; set; }
    public string? ConditionSource { get; set; }
    public string? RegexPattern { get; set; }
    public string? TransformationsJson { get; set; }
}
