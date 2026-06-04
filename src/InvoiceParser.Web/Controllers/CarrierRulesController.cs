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
    private readonly GenericInvoiceParser _parser;
    private readonly ILogger<CarrierRulesController> _logger;

    private static readonly string[] KnownSummaryFields =
    {
        "invoice_number", "carrier_account", "invoice_date", "invoice_st_dtm",
        "invoice_end_dtm", "invoice_due_dtm", "beg_bal", "payment",
        "prev_adj", "curr_adj", "curr_chg", "curr_tax", "end_bal",
    };

    private static readonly string[] KnownLineItemFields =
    {
        "line_number", "location",
    };

    public CarrierRulesController(IInvoiceRepository repo,
        GenericInvoiceParser parser,
        ILogger<CarrierRulesController> logger)
    {
        _repo = repo;
        _parser = parser;
        _logger = logger;
    }

    // GET /CarrierRules/Index/{carrierId}
    public async Task<IActionResult> Index(int carrierId)
    {
        var carrier = await _repo.GetCarrierByIdAsync(carrierId);
        if (carrier == null) return NotFound();

        var rules = await _repo.GetAllRulesForCarrierAsync(carrierId);
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

    // GET /CarrierRules/Edit/{id}
    public async Task<IActionResult> Edit(int id)
    {
        var rule = await _repo.GetRuleByIdAsync(id);
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
            ? (await _repo.GetRuleByIdAsync(vm.Id))!
            : new VendorParsingRule { CarrierId = vm.CarrierId };

        if (rule == null) return NotFound();

        MapFromViewModel(vm, rule);

        if (vm.Id > 0)
            await _repo.UpdateRuleAsync(rule);
        else
            await _repo.SaveRuleAsync(rule);

        TempData["SuccessMessage"] = $"Rule '{rule.FieldName}' saved.";
        return RedirectToAction("Index", new { carrierId = rule.CarrierId });
    }

    // POST /CarrierRules/Delete/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var rule = await _repo.GetRuleByIdAsync(id);
        if (rule == null) return NotFound();
        var carrierId = rule.CarrierId;
        await _repo.DeleteRuleAsync(id);
        TempData["SuccessMessage"] = "Rule deleted.";
        return RedirectToAction("Index", new { carrierId });
    }

    // POST /CarrierRules/Toggle/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var rule = await _repo.GetRuleByIdAsync(id);
        if (rule == null) return NotFound();
        rule.IsActive = !rule.IsActive;
        await _repo.UpdateRuleAsync(rule);
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
                    if (!ConditionEvaluator.Evaluate(line, request.ConditionType, request.RegexPattern))
                        continue;
                    extracted = line;
                }

                // Apply transforms
                if (!string.IsNullOrWhiteSpace(request.TransformationsJson))
                {
                    var steps = TransformPipeline.Deserialize(request.TransformationsJson);
                    if (steps != null && extracted != null)
                        extracted = TransformPipeline.Apply(extracted, steps);
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

        await _repo.SaveRuleAsync(rule);

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
            new { Value = "t_invoice", Text = "Invoice Summary (t_invoice)" },
            new { Value = "t_charge",  Text = "Line Item / Charge (t_charge)" },
        }, "Value", "Text");

        ViewBag.FieldTypes = new SelectList(new[]
        {
            new { Value = "string",          Text = "String" },
            new { Value = "date",            Text = "Date" },
            new { Value = "decimal",         Text = "Decimal / Amount" },
            new { Value = "skip",            Text = "Skip (ignore matching lines)" },
            new { Value = "line_anchor",     Text = "Line Anchor (sets Line Number context)" },
            new { Value = "location_anchor", Text = "Location Anchor (sets Location context)" },
        }, "Value", "Text");

        ViewBag.KnownFields = KnownSummaryFields
            .Concat(KnownLineItemFields)
            .Select(f => new SelectListItem(f, f))
            .ToList();

        ViewBag.TransformTypes = new[]
        {
            new { Value = "remove_prefix",   Label = "Remove Prefix",        HasValue1 = true,  HasValue2 = false, Value1Label = "Prefix to remove" },
            new { Value = "remove_suffix",   Label = "Remove Suffix",        HasValue1 = true,  HasValue2 = false, Value1Label = "Suffix to remove" },
            new { Value = "replace",         Label = "Replace Text",         HasValue1 = true,  HasValue2 = true,  Value1Label = "Find text" },
            new { Value = "trim",            Label = "Trim Whitespace",      HasValue1 = false, HasValue2 = false, Value1Label = "" },
            new { Value = "extract_between", Label = "Extract Between",      HasValue1 = true,  HasValue2 = true,  Value1Label = "Start marker" },
            new { Value = "regex_extract",   Label = "Regex Extract (Grp 1)",HasValue1 = true,  HasValue2 = false, Value1Label = "Regex pattern" },
            new { Value = "convert_date",    Label = "Convert Date Format",  HasValue1 = true,  HasValue2 = true,  Value1Label = "Input format (e.g. MM/dd/yy)" },
            new { Value = "to_upper",        Label = "To Upper Case",        HasValue1 = false, HasValue2 = false, Value1Label = "" },
            new { Value = "to_lower",        Label = "To Lower Case",        HasValue1 = false, HasValue2 = false, Value1Label = "" },
        };
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

public class RuleTestRequest
{
    public int CarrierId { get; set; }
    public string? PdfText { get; set; }
    public string? ConditionType { get; set; }
    public string? ConditionSource { get; set; }
    public string? RegexPattern { get; set; }
    public string? TransformationsJson { get; set; }
}
