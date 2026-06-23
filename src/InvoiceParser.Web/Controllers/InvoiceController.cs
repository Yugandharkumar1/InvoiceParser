using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services;
using InvoiceParser.Web.Models;
using InvoiceParser.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InvoiceParser.Web.Controllers;

public class InvoiceController : Controller
{
    private readonly InvoiceService _invoiceService;
    private readonly FeedbackProcessor _feedbackProcessor;
    private readonly PythonInvoiceIntegrationService _pythonIntegration;
    private readonly ICarrierRuleStore _ruleStore;
    private readonly ILogger<InvoiceController> _logger;

    public InvoiceController(InvoiceService invoiceService, FeedbackProcessor feedbackProcessor,
        PythonInvoiceIntegrationService pythonIntegration,
        ICarrierRuleStore ruleStore,
        ILogger<InvoiceController> logger)
    {
        _invoiceService = invoiceService;
        _feedbackProcessor = feedbackProcessor;
        _pythonIntegration = pythonIntegration;
        _ruleStore = ruleStore;
        _logger = logger;
    }

    public async Task<IActionResult> Upload()
    {
        var vm = await BuildUploadViewModelAsync();
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(UploadViewModel model)
    {
        if (model.PdfFile == null || model.PdfFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.PdfFile), "Please select a file (PDF, JPEG, PNG, or TIFF).");
            var vm = await BuildUploadViewModelAsync();
            vm.CustomerId = model.CustomerId;
            vm.CarrierId = model.CarrierId;
            return View(vm);
        }

        if (model.CustomerId <= 0)
        {
            ModelState.AddModelError(nameof(model.CustomerId), "Please select a customer.");
            var vm = await BuildUploadViewModelAsync();
            return View(vm);
        }

        if (model.CarrierId <= 0)
        {
            // Attempt server-side auto-detection as a last-resort fallback
            try
            {
                using var detectStream = model.PdfFile.OpenReadStream();
                var rawText = await _invoiceService.ExtractTextFromFileAsync(detectStream, model.PdfFile.FileName);
                var detected = await _invoiceService.DetectCarrierAsync(rawText);
                if (detected != null)
                    model.CarrierId = detected.Id;
            }
            catch { /* detection is best-effort */ }

            if (model.CarrierId <= 0)
            {
                ModelState.AddModelError(nameof(model.CarrierId), "Carrier could not be detected automatically. Please select the carrier.");
                var vm = await BuildUploadViewModelAsync();
                vm.CustomerId = model.CustomerId;
                return View(vm);
            }
        }

        using var stream = model.PdfFile.OpenReadStream();
        var parseResult = await _invoiceService.ParseFileAsync(stream, model.CarrierId, model.PdfFile.FileName);
        var parsed = parseResult.Parsed;

        var carrier = await _invoiceService.GetCarrierByIdAsync(model.CarrierId);
        var customers = await _invoiceService.GetCustomersAsync();
        var customer = customers.FirstOrDefault(c => c.Id == model.CustomerId);

        var review = new ReviewViewModel
        {
            CustomerId = model.CustomerId,
            CustomerName = customer?.Name,
            CarrierId = model.CarrierId,
            CarrierName = carrier?.Name,
            CarrierCode = carrier?.Code,
            PdfText = parseResult.PdfText,
        };

        if (parsed.SummaryFields.TryGetValue("invoice_number", out var invNum))
            review.InvoiceNumber = invNum;
        if (parsed.SummaryFields.TryGetValue("carrier_account", out var acct))
            review.CarrierAccount = acct;
        if (parsed.SummaryFields.TryGetValue("invoice_date", out var invDate) &&
            DateTime.TryParse(invDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invDtParsed))
            review.InvoiceDate = invDtParsed.ToString("yyyy-MM-dd");
        if (parsed.SummaryFields.TryGetValue("invoice_st_dtm", out var stDate) &&
            DateTime.TryParse(stDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stDtParsed))
            review.InvoiceStartDate = stDtParsed.ToString("yyyy-MM-dd");
        if (parsed.SummaryFields.TryGetValue("invoice_end_dtm", out var endDate) &&
            DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDtParsed))
            review.InvoiceEndDate = endDtParsed.ToString("yyyy-MM-dd");
        if (parsed.SummaryFields.TryGetValue("invoice_due_dtm", out var dueDate) &&
            DateTime.TryParse(dueDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDtParsed))
            review.InvoiceDueDate = dueDtParsed.ToString("yyyy-MM-dd");
        review.BeginningBalance = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("beg_bal")) ?? "0.00";
        review.Payment = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("payment")) ?? "0.00";
        review.PreviousAdjustments = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("prev_adj")) ?? "0.00";
        review.CurrentAdjustments = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("curr_adj")) ?? "0.00";
        review.CurrentCharges = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("curr_chg")) ?? "0.00";
        review.CurrentTax = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("curr_tax")) ?? "0.00";
        review.EndingBalance = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("end_bal")) ?? "0.00";

        review.Charges = parsed.Charges.Select(c => new ReviewChargeItem
        {
            ChargeDescription = c.ChargeDescription,
            Amount = c.Amount?.ToString("F2"),
            Line = c.Line,
            Location = c.Location,
        }).ToList();

        review.Usages = parsed.Usages.Select(u => new ReviewUsageItem
        {
            EmployeeName = u.EmployeeName,
            LineNumber = u.LineNumber,
            UsageType = u.UsageType,
            UsocName = u.UsocName,
            UsageLimit = u.UsageLimit,
            UsageAmount = u.UsageAmount,
            Charge = u.Charge,
        }).ToList();

        review.Inventories = parsed.Inventories.Select(inv => new ReviewInventoryItem
        {
            EmployeeName = inv.EmployeeName,
            LineNumber = inv.ReferenceNumber,
            PlanName = inv.PlanName,
            PlanAmount = !string.IsNullOrWhiteSpace(inv.EmployeeName) && string.IsNullOrWhiteSpace(inv.PlanAmount)
                ? "0.00"
                : inv.PlanAmount,
            ServiceType = inv.ServiceType,
        }).ToList();

        if (parseResult.Python != null)
        {
            review.PythonParseId = parseResult.Python.ParseId;
            review.PythonOcrJson = parseResult.Python.OcrJson;
            review.PythonSource = parseResult.Python.Source;
            review.PythonVendorHint = parseResult.Python.VendorHint;
        }

        // Store original parser values for feedback comparison
        review.Orig_InvoiceNumber = review.InvoiceNumber;
        review.Orig_CarrierAccount = review.CarrierAccount;
        review.Orig_InvoiceDate = review.InvoiceDate;
        review.Orig_InvoiceStartDate = review.InvoiceStartDate;
        review.Orig_InvoiceEndDate = review.InvoiceEndDate;
        review.Orig_InvoiceDueDate = review.InvoiceDueDate;
        review.Orig_BeginningBalance = review.BeginningBalance;
        review.Orig_Payment = review.Payment;
        review.Orig_PreviousAdjustments = review.PreviousAdjustments;
        review.Orig_CurrentAdjustments = review.CurrentAdjustments;
        review.Orig_CurrentCharges = review.CurrentCharges;
        review.Orig_CurrentTax = review.CurrentTax;
        review.Orig_EndingBalance = review.EndingBalance;
        review.Orig_ChargeCount = review.Charges.Count;
        review.OriginalChargesJson = JsonSerializer.Serialize(
            review.Charges.Select(c => new OriginalChargeItem
            {
                Description = c.ChargeDescription,
                Amount = c.Amount,
            }).ToList());

        return View("Review", review);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ReviewViewModel model)
    {
        _logger.LogInformation("Save requested: InvoiceNumber={InvNum}, CarrierId={CarrierId}, CustomerId={CustId}",
            model.InvoiceNumber, model.CarrierId, model.CustomerId);

        var existing = await _invoiceService.FindDuplicateInvoiceAsync(model.InvoiceNumber, model.CarrierId);
        if (existing != null && !model.OverwriteExisting)
        {
            _logger.LogWarning("Duplicate invoice detected: InvoiceNumber={InvNum}, ExistingId={Id}",
                model.InvoiceNumber, existing.Id);
            model.OverwriteExisting = false; // ensure flag is in model for the view
            TempData["DuplicateInvoiceId"] = existing.Id;
            TempData["DuplicateInvoiceNumber"] = model.InvoiceNumber;
            return View("Review", model);
        }

        // If overwriting, delete the old record first
        if (existing != null && model.OverwriteExisting)
        {
            _logger.LogInformation("Overwriting existing invoice {Id} ({InvNum})", existing.Id, model.InvoiceNumber);
            await _invoiceService.DeleteInvoiceAsync(existing.Id);
        }

        // Persist any summary fields the user intentionally left blank
        if (!string.IsNullOrWhiteSpace(model.SkippedSummaryFields) && model.CarrierId > 0)
        {
            var skipped = model.SkippedSummaryFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => f.Length > 0);
            await _ruleStore.AddSkippedSummaryFieldsAsync(model.CarrierId, skipped);
        }

        try
        {
            var invoice = new Invoice
            {
                CustomerId = model.CustomerId,
                CarrierId = model.CarrierId,
                CarrierName = model.CarrierName,
                CarrierCode = model.CarrierCode,
                CarrierAccount = model.CarrierAccount,
                InvoiceNumber = model.InvoiceNumber,
            };

            if (DateTime.TryParse(model.InvoiceDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invDt))
                invoice.InvoiceDate = invDt;
            if (DateTime.TryParse(model.InvoiceStartDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stDt))
                invoice.InvoiceStartDate = stDt;
            if (DateTime.TryParse(model.InvoiceEndDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDt))
                invoice.InvoiceEndDate = endDt;
            if (DateTime.TryParse(model.InvoiceDueDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDt))
                invoice.InvoiceDueDate = dueDt;

            if (MonetaryParser.TryParse(model.BeginningBalance, out var begBal)) invoice.BeginningBalance = begBal;
            if (MonetaryParser.TryParse(model.Payment, out var pmt)) invoice.Payment = pmt;
            if (MonetaryParser.TryParse(model.PreviousAdjustments, out var prevAdj)) invoice.PreviousAdjustments = prevAdj;
            if (MonetaryParser.TryParse(model.CurrentAdjustments, out var currAdj)) invoice.CurrentAdjustments = currAdj;
            if (MonetaryParser.TryParse(model.CurrentCharges, out var currChg)) invoice.CurrentCharges = currChg;
            if (MonetaryParser.TryParse(model.CurrentTax, out var currTax)) invoice.CurrentTax = currTax;
            if (MonetaryParser.TryParse(model.EndingBalance, out var endBal)) invoice.EndingBalance = endBal;

            var charges = model.Charges
                .Where(c => !string.IsNullOrWhiteSpace(c.ChargeDescription))
                .Select(c =>
                {
                    var charge = new Charge
                    {
                        ChargeDescription = c.ChargeDescription,
                        Line = c.Line,
                        Location = c.Location,
                    };
                    if (MonetaryParser.TryParse(c.Amount, out var amt))
                        charge.Amount = amt;
                    return charge;
                }).ToList();

            invoice.PdfText = model.PdfText;

            var usageEntities = new List<Usage>();
            if (model.Usages.Count > 0)
            {
                foreach (var u in model.Usages)
                {
                    if (string.Equals(u.UsageLimit?.Trim(), "Unlimited", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(u.UsageLimit))
                        u.UsageLimit = "-1";
                }

                usageEntities = model.Usages.Select(u => new Usage
                {
                    LineNumber = u.LineNumber,
                    UsocName = u.UsocName,
                    UsageLimit = u.UsageLimit,
                    UsageAmount = u.UsageAmount,
                    Charge = u.Charge,
                    UsageType = u.UsageType,
                    CarrierId = model.CarrierId,
                    AddDate = DateTime.Now,
                }).ToList();
            }

            var inventoryEntities = new List<Inventory>();
            if (model.Inventories.Count > 0)
            {
                inventoryEntities = model.Inventories
                    .Where(inv => !string.IsNullOrWhiteSpace(inv.LineNumber))
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
                            CustomerId = model.CustomerId,
                            ReferenceNumber = inv.LineNumber!,
                            EmployeeName = inv.EmployeeName,
                            InventoryName = inv.PlanName,
                            ServiceType = inv.ServiceType,
                            LocationName = planAmt,
                        };
                    }).ToList();
            }

            var saved = await _invoiceService.SaveAllAsync(invoice, charges, usageEntities, inventoryEntities);

            _logger.LogInformation("Invoice saved: Id={Id}, InvoiceNumber={InvNum}, Charges={Charges}, Usages={Usages}, Inventories={Inv}",
                saved.Id, saved.InvoiceNumber, charges.Count, usageEntities.Count, inventoryEntities.Count);

            // Post-save: feedback learning and Python integration are non-critical.
            // Errors here must never prevent the user from reaching the Detail page.
            try
            {
                if (!string.IsNullOrWhiteSpace(model.PdfText) && model.CarrierId > 0)
                {
                    var confirmedFields = new Dictionary<string, string?>
                    {
                        ["invoice_number"] = model.InvoiceNumber,
                        ["carrier_account"] = model.CarrierAccount,
                        ["invoice_date"] = model.InvoiceDate,
                        ["invoice_st_dtm"] = model.InvoiceStartDate,
                        ["invoice_end_dtm"] = model.InvoiceEndDate,
                        ["invoice_due_dtm"] = model.InvoiceDueDate,
                        ["beg_bal"] = model.BeginningBalance,
                        ["payment"] = model.Payment,
                        ["prev_adj"] = model.PreviousAdjustments,
                        ["curr_adj"] = model.CurrentAdjustments,
                        ["curr_chg"] = model.CurrentCharges,
                        ["curr_tax"] = model.CurrentTax,
                        ["end_bal"] = model.EndingBalance,
                    };

                    var originalFields = new Dictionary<string, string?>
                    {
                        ["invoice_number"] = model.Orig_InvoiceNumber,
                        ["carrier_account"] = model.Orig_CarrierAccount,
                        ["invoice_date"] = model.Orig_InvoiceDate,
                        ["invoice_st_dtm"] = model.Orig_InvoiceStartDate,
                        ["invoice_end_dtm"] = model.Orig_InvoiceEndDate,
                        ["invoice_due_dtm"] = model.Orig_InvoiceDueDate,
                        ["beg_bal"] = model.Orig_BeginningBalance,
                        ["payment"] = model.Orig_Payment,
                        ["prev_adj"] = model.Orig_PreviousAdjustments,
                        ["curr_adj"] = model.Orig_CurrentAdjustments,
                        ["curr_chg"] = model.Orig_CurrentCharges,
                        ["curr_tax"] = model.Orig_CurrentTax,
                        ["end_bal"] = model.Orig_EndingBalance,
                    };

                    var chargeFeedback = BuildChargeFeedback(model);

                    await _invoiceService.ProcessFeedbackAndLearnAsync(
                        model.PdfText, model.CarrierId, confirmedFields, originalFields, chargeFeedback);

                    if (!string.IsNullOrWhiteSpace(model.PythonOcrJson))
                    {
                        var pyTrace = new PythonParseTrace
                        {
                            ParseId = model.PythonParseId,
                            OcrJson = model.PythonOcrJson,
                            Source = model.PythonSource,
                            VendorHint = model.PythonVendorHint,
                        };
                        await _pythonIntegration.TrySubmitFeedbackAsync(
                            saved, pyTrace, confirmedFields, model.CarrierName, HttpContext.RequestAborted);
                    }
                }
            }
            catch (Exception feedbackEx)
            {
                // Log but do not surface — invoice is already persisted.
                _logger.LogWarning(feedbackEx, "Post-save feedback/learning step failed for invoice {Id}; invoice was saved successfully.", saved.Id);
            }

            TempData["SuccessMessage"] = $"Invoice #{saved.InvoiceNumber ?? saved.Id.ToString()} saved successfully.";
            return RedirectToAction("Detail", new { id = saved.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save invoice InvoiceNumber={InvNum}", model.InvoiceNumber);
            TempData["ErrorMessage"] = "An error occurred while saving the invoice. Please try again.";
            return View("Review", model);
        }
    }

    private static List<ChargeFeedback> BuildChargeFeedback(ReviewViewModel model)
    {
        var chargeFeedback = new List<ChargeFeedback>();
        var origCharges = new List<OriginalChargeItem>();
        if (!string.IsNullOrWhiteSpace(model.OriginalChargesJson))
        {
            try { origCharges = JsonSerializer.Deserialize<List<OriginalChargeItem>>(model.OriginalChargesJson) ?? new(); }
            catch { /* ignore deserialization errors */ }
        }

        var submittedCharges = model.Charges
            .Where(c => !string.IsNullOrWhiteSpace(c.ChargeDescription))
            .ToList();

        for (int i = 0; i < origCharges.Count; i++)
        {
            var orig = origCharges[i];
            var matched = i < submittedCharges.Count ? submittedCharges[i] : null;

            if (matched == null)
            {
                chargeFeedback.Add(new ChargeFeedback
                {
                    OriginalDescription = orig.Description,
                    CorrectedDescription = null,
                    Action = "deleted",
                });
            }
            else if (!string.Equals(orig.Description?.Trim(), matched.ChargeDescription?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                chargeFeedback.Add(new ChargeFeedback
                {
                    OriginalDescription = orig.Description,
                    CorrectedDescription = matched.ChargeDescription,
                    Action = "corrected",
                });
            }
        }

        for (int i = origCharges.Count; i < submittedCharges.Count; i++)
        {
            chargeFeedback.Add(new ChargeFeedback
            {
                OriginalDescription = null,
                CorrectedDescription = submittedCharges[i].ChargeDescription,
                Action = "added",
            });
        }

        return chargeFeedback;
    }

    /// <summary>
    /// Re-parses already-extracted PDF text with the carrier's current rule set.
    /// Called from the Invoice Review page after a new rule is saved, so the user sees
    /// updated results without re-uploading the PDF file.
    /// </summary>
    [HttpPost("api/invoice/reparse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reparse([FromForm] int carrierId, [FromForm] string? pdfText)
    {
        if (carrierId <= 0)
            return BadRequest(new { error = "carrierId is required." });

        if (string.IsNullOrWhiteSpace(pdfText))
            return BadRequest(new { error = "pdfText is required." });

        try
        {
            var result = await _invoiceService.ParseTextAsync(pdfText, carrierId);
            var parsed = result.Parsed;

            // Convert raw date strings to yyyy-MM-dd so <input type="date"> fields accept them
            static string? NormalizeDate(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? dt.ToString("yyyy-MM-dd")
                    : null;
            }

            return Ok(new
            {
                summary = new
                {
                    invoiceNumber    = parsed.SummaryFields.GetValueOrDefault("invoice_number"),
                    carrierAccount   = parsed.SummaryFields.GetValueOrDefault("carrier_account"),
                    invoiceDate      = NormalizeDate(parsed.SummaryFields.GetValueOrDefault("invoice_date")),
                    invoiceStartDate = NormalizeDate(parsed.SummaryFields.GetValueOrDefault("invoice_st_dtm")),
                    invoiceEndDate   = NormalizeDate(parsed.SummaryFields.GetValueOrDefault("invoice_end_dtm")),
                    invoiceDueDate   = NormalizeDate(parsed.SummaryFields.GetValueOrDefault("invoice_due_dtm")),
                    beginningBalance = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("beg_bal")),
                    payment          = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("payment")),
                    previousAdjustments = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("prev_adj")),
                    currentAdjustments  = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("curr_adj")),
                    currentCharges = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("curr_chg")),
                    currentTax     = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("curr_tax")),
                    endingBalance  = MonetaryParser.Clean(parsed.SummaryFields.GetValueOrDefault("end_bal")),
                },
                charges = parsed.Charges.Select(c => new
                {
                    description = c.ChargeDescription,
                    amount      = c.Amount?.ToString("F2"),
                    line        = c.Line,
                    location    = c.Location,
                }).ToList(),
                chargeCount = parsed.Charges.Count,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reparse failed for carrier {CarrierId}.", carrierId);
            return StatusCode(500, new { error = "Reparse failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Lightweight endpoint: extract text from an uploaded file and return the best-matching carrier.
    /// Called by client-side JS immediately after the user picks a file on the Upload form.
    /// </summary>
    [HttpPost("api/invoice/detect-carrier")]
    public async Task<IActionResult> DetectCarrier(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        try
        {
            using var stream = file.OpenReadStream();
            var text = await _invoiceService.ExtractTextFromFileAsync(stream, file.FileName);
            var carrier = await _invoiceService.DetectCarrierAsync(text);

            if (carrier == null)
                return Ok(new { detected = false, message = "Carrier not recognised — please select manually." });

            return Ok(new { detected = true, carrierId = carrier.Id, carrierName = carrier.Name, carrierCode = carrier.Code });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Carrier auto-detection failed for '{FileName}'.", file?.FileName);
            return Ok(new { detected = false, message = "Detection failed — please select the carrier manually." });
        }
    }

    [HttpPost("api/invoice/parse")]
    public async Task<IActionResult> ParseApi(IFormFile? file, [FromForm] int carrierId)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded. Send a PDF or image file." });

        if (carrierId <= 0)
            return BadRequest(new { error = "carrierId is required and must be greater than 0." });

        try
        {
            using var stream = file.OpenReadStream();
            var result = await _invoiceService.ParseFileAsync(stream, carrierId, file.FileName);

            return Ok(new
            {
                InvoiceNumber = result.Parsed.SummaryFields.GetValueOrDefault("invoice_number"),
                CarrierAccount = result.Parsed.SummaryFields.GetValueOrDefault("carrier_account"),
                InvoiceDate = result.Parsed.SummaryFields.GetValueOrDefault("invoice_date"),
                InvoiceStartDate = result.Parsed.SummaryFields.GetValueOrDefault("invoice_st_dtm"),
                InvoiceEndDate = result.Parsed.SummaryFields.GetValueOrDefault("invoice_end_dtm"),
                DueDate = result.Parsed.SummaryFields.GetValueOrDefault("invoice_due_dtm"),
                BeginningBalance = result.Parsed.SummaryFields.GetValueOrDefault("beg_bal"),
                Payment = result.Parsed.SummaryFields.GetValueOrDefault("payment"),
                PreviousAdjustments = result.Parsed.SummaryFields.GetValueOrDefault("prev_adj"),
                CurrentAdjustments = result.Parsed.SummaryFields.GetValueOrDefault("curr_adj"),
                CurrentCharges = result.Parsed.SummaryFields.GetValueOrDefault("curr_chg"),
                TaxAmount = result.Parsed.SummaryFields.GetValueOrDefault("curr_tax"),
                TotalAmount = result.Parsed.SummaryFields.GetValueOrDefault("end_bal"),
                Charges = result.Parsed.Charges.Select(c => new
                {
                    Description = c.ChargeDescription,
                    c.Amount,
                    c.Line,
                    c.Location,
                }),
                Usages = result.Parsed.Usages.Select(u => new
                {
                    u.LineNumber,
                    u.UsocName,
                    u.UsageAmount,
                    u.UsageLimit,
                    u.Charge,
                    u.UsageType,
                }),
                Inventories = result.Parsed.Inventories.Select(i => new
                {
                    i.ReferenceNumber,
                    i.EmployeeName,
                    i.PlanName,
                    i.PlanAmount,
                    i.ServiceType,
                }),
                ExtractedTextLength = result.PdfText?.Length ?? 0,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API parse failed for file {FileName}", file.FileName);
            return StatusCode(500, new { error = "Failed to parse the uploaded file.", details = ex.Message });
        }
    }

    public async Task<IActionResult> Detail(int id)
    {
        var invoice = await _invoiceService.GetInvoiceByIdAsync(id);
        if (invoice == null)
            return NotFound();
        return View(invoice);
    }

    public async Task<IActionResult> ExportExcel(int id)
    {
        var invoice = await _invoiceService.GetInvoiceByIdAsync(id);
        if (invoice == null)
            return NotFound();

        // Resolve customer name
        var customers = await _invoiceService.GetCustomersAsync();
        var customerName = customers.FirstOrDefault(c => c.Id == invoice.CustomerId)?.Name ?? "";

        using var workbook = new XLWorkbook();

        // ── Sheet 1: Invoice Summary (flat column layout) ─────────────────────
        var ws1 = workbook.Worksheets.Add("Summary");
        ws1.Style.Font.FontName = "Calibri";

        string[] summaryHeaders = {
            "Customer", "Carrier", "Account Number", "Invoice Number",
            "Invoice Date", "Invoice Start Date", "Invoice End Date", "Due Date",
            "Previous Balance", "Payments", "Previous Adjustments", "Current Adjustments",
            "Current Charges", "Taxes", "Balance Due"
        };

        // Header row
        for (int c = 0; c < summaryHeaders.Length; c++)
        {
            var hCell = ws1.Cell(1, c + 1);
            hCell.Value = summaryHeaders[c];
            hCell.Style.Font.Bold = true;
            hCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            hCell.Style.Font.FontColor = XLColor.White;
            hCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            hCell.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        }

        // Data row
        int col = 1;
        ws1.Cell(2, col++).Value = customerName;
        ws1.Cell(2, col++).Value = invoice.CarrierName ?? "";
        ws1.Cell(2, col++).Value = invoice.CarrierAccount ?? "";
        ws1.Cell(2, col++).Value = invoice.InvoiceNumber ?? "";

        void SetDate(int c, DateTime? dt) {
            if (dt.HasValue) {
                ws1.Cell(2, c).Value = dt.Value;
                ws1.Cell(2, c).Style.NumberFormat.Format = "MM/DD/YYYY";
            }
        }
        SetDate(col++, invoice.InvoiceDate);
        SetDate(col++, invoice.InvoiceStartDate);
        SetDate(col++, invoice.InvoiceEndDate);
        SetDate(col++, invoice.InvoiceDueDate);

        void SetMoney(int c, decimal? val) {
            ws1.Cell(2, c).Value = val ?? 0m;
            ws1.Cell(2, c).Style.NumberFormat.Format = "#,##0.00";
        }
        SetMoney(col++, invoice.BeginningBalance);
        SetMoney(col++, invoice.Payment);
        SetMoney(col++, invoice.PreviousAdjustments);
        SetMoney(col++, invoice.CurrentAdjustments);
        SetMoney(col++, invoice.CurrentCharges);
        SetMoney(col++, invoice.CurrentTax);

        // Balance Due — bold + highlighted
        ws1.Cell(2, col).Value = invoice.EndingBalance ?? 0m;
        ws1.Cell(2, col).Style.NumberFormat.Format = "#,##0.00";
        ws1.Cell(2, col).Style.Font.Bold = true;
        ws1.Cell(2, col).Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEEFF");

        // Border around data row and auto-fit
        ws1.Range(1, 1, 2, summaryHeaders.Length).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws1.Range(1, 1, 2, summaryHeaders.Length).Style.Border.InsideBorder  = XLBorderStyleValues.Hair;
        ws1.SheetView.FreezeRows(1);
        ws1.Columns().AdjustToContents();
        // Ensure minimum widths
        for (int c = 1; c <= summaryHeaders.Length; c++)
            if (ws1.Column(c).Width < 14) ws1.Column(c).Width = 14;

        // ── Sheet 2: Line Details ─────────────────────────────────────────────
        var ws2 = workbook.Worksheets.Add("Line Details");
        ws2.Style.Font.FontName = "Calibri";

        // Column headers
        string[] headers = {
            "Carrier Account", "Description", "Line", "Location",
            "Usage Type", "Usage Limit", "Usage Amount",
            "Employee Name", "Plan Name", "Charge Amount"
        };

        for (int c = 0; c < headers.Length; c++)
        {
            var hCell = ws2.Cell(1, c + 1);
            hCell.Value = headers[c];
            hCell.Style.Font.Bold = true;
            hCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            hCell.Style.Font.FontColor = XLColor.White;
            hCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            hCell.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        }

        int row = 2;
        string acct = invoice.CarrierAccount ?? "";

        // --- Charge rows (light blue tint) ---
        foreach (var ch in invoice.Charges)
        {
            ws2.Cell(row, 1).Value  = acct;
            ws2.Cell(row, 2).Value  = ch.ChargeDescription ?? "";
            ws2.Cell(row, 3).Value  = ch.Line ?? "";
            ws2.Cell(row, 4).Value  = ch.Location ?? "";
            // cols 5-9 left blank for charge rows
            if (ch.Amount.HasValue)
            {
                ws2.Cell(row, 10).Value = ch.Amount.Value;
                ws2.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
            }
            ws2.Range(row, 1, row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBF3FB");
            row++;
        }

        // --- Usage rows (light green tint) ---
        foreach (var u in invoice.Usages)
        {
            ws2.Cell(row, 1).Value  = acct;
            ws2.Cell(row, 3).Value  = u.LineNumber ?? "";
            ws2.Cell(row, 5).Value  = u.UsageType ?? "";
            ws2.Cell(row, 6).Value  = u.UsageLimit ?? "";
            ws2.Cell(row, 7).Value  = u.UsageAmount ?? "";
            ws2.Cell(row, 9).Value  = u.UsocName ?? "";     // Plan Name
            if (!string.IsNullOrWhiteSpace(u.Charge) &&
                decimal.TryParse(u.Charge, NumberStyles.Any, CultureInfo.InvariantCulture, out var uChg))
            {
                ws2.Cell(row, 10).Value = uChg;
                ws2.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
            }
            ws2.Range(row, 1, row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBF9EB");
            row++;
        }

        // --- Inventory rows (light yellow tint) ---
        foreach (var inv in invoice.Inventories)
        {
            ws2.Cell(row, 1).Value  = acct;
            ws2.Cell(row, 3).Value  = inv.ReferenceNumber ?? "";   // Line
            ws2.Cell(row, 8).Value  = inv.EmployeeName ?? "";
            ws2.Cell(row, 9).Value  = inv.InventoryName ?? "";     // Plan Name
            ws2.Range(row, 1, row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFBE6");
            row++;
        }

        // Auto-fit + freeze header row
        ws2.SheetView.FreezeRows(1);
        ws2.Columns().AdjustToContents(1, row);
        ws2.Column(1).Width  = Math.Max(ws2.Column(1).Width,  18);
        ws2.Column(2).Width  = Math.Max(ws2.Column(2).Width,  36);
        ws2.Column(10).Width = Math.Max(ws2.Column(10).Width, 16);

        // Add a thin border around all data rows
        if (row > 2)
        {
            ws2.Range(1, 1, row - 1, headers.Length)
               .Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws2.Range(1, 1, row - 1, headers.Length)
               .Style.Border.InsideBorder  = XLBorderStyleValues.Hair;
        }

        // ── Stream the file ───────────────────────────────────────────────────
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        ms.Position = 0;

        var fileName = $"Invoice_{invoice.InvoiceNumber ?? invoice.Id.ToString()}_{DateTime.Now:yyyyMMdd}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    public async Task<IActionResult> Index()
    {
        var invoices = await _invoiceService.GetAllInvoicesAsync();
        return View(invoices);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitFeedback(ReviewViewModel model, string feedbackText)
    {
        if (string.IsNullOrWhiteSpace(feedbackText))
        {
            TempData["ErrorMessage"] = "Feedback text cannot be empty.";
            return View("Review", model);
        }

        var originalFields = new Dictionary<string, string?>
        {
            ["invoice_number"] = model.Orig_InvoiceNumber,
            ["carrier_account"] = model.Orig_CarrierAccount,
            ["invoice_date"] = model.Orig_InvoiceDate,
            ["invoice_st_dtm"] = model.Orig_InvoiceStartDate,
            ["invoice_end_dtm"] = model.Orig_InvoiceEndDate,
            ["invoice_due_dtm"] = model.Orig_InvoiceDueDate,
            ["beg_bal"] = model.Orig_BeginningBalance,
            ["payment"] = model.Orig_Payment,
            ["prev_adj"] = model.Orig_PreviousAdjustments,
            ["curr_adj"] = model.Orig_CurrentAdjustments,
            ["curr_chg"] = model.Orig_CurrentCharges,
            ["curr_tax"] = model.Orig_CurrentTax,
            ["end_bal"] = model.Orig_EndingBalance,
        };

        var confirmedFields = new Dictionary<string, string?>
        {
            ["invoice_number"] = model.InvoiceNumber,
            ["carrier_account"] = model.CarrierAccount,
            ["invoice_date"] = model.InvoiceDate,
            ["invoice_st_dtm"] = model.InvoiceStartDate,
            ["invoice_end_dtm"] = model.InvoiceEndDate,
            ["invoice_due_dtm"] = model.InvoiceDueDate,
            ["beg_bal"] = model.BeginningBalance,
            ["payment"] = model.Payment,
            ["prev_adj"] = model.PreviousAdjustments,
            ["curr_adj"] = model.CurrentAdjustments,
            ["curr_chg"] = model.CurrentCharges,
            ["curr_tax"] = model.CurrentTax,
            ["end_bal"] = model.EndingBalance,
        };

        var feedback = new InvoiceFeedback
        {
            CarrierId = model.CarrierId,
            FeedbackText = feedbackText.Trim(),
            PdfText = model.PdfText,
            OriginalFieldsJson = JsonSerializer.Serialize(originalFields),
            ConfirmedFieldsJson = JsonSerializer.Serialize(confirmedFields),
            OriginalChargesJson = model.OriginalChargesJson,
            CreatedAt = DateTime.UtcNow,
        };

        await _invoiceService.SaveUserFeedbackAsync(feedback);

        // Extract corrections from feedback text and apply to the model
        var corrections = _feedbackProcessor.ExtractCorrections(feedbackText);
        var appliedCorrections = new List<string>();

        foreach (var (property, value) in corrections)
        {
            var prop = typeof(ReviewViewModel).GetProperty(property);
            if (prop == null) continue;

            var currentValue = prop.GetValue(model) as string;
            if (currentValue != value)
            {
                prop.SetValue(model, value);
                appliedCorrections.Add(FormatFieldName(property));
            }
        }

        if (appliedCorrections.Count > 0)
        {
            TempData["FeedbackSuccess"] = $"Feedback recorded! Auto-corrected: {string.Join(", ", appliedCorrections)}. Please verify the changes and click 'Confirm & Save'.";
        }
        else
        {
            TempData["FeedbackSuccess"] = "Feedback recorded. No auto-corrections could be extracted — please manually correct the fields and save.";
        }

        return View("Review", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProcessFeedback()
    {
        var (processed, rulesGenerated) = await _invoiceService.ProcessAllUnprocessedFeedbackAsync();

        if (processed > 0)
            TempData["SuccessMessage"] = $"Processed {processed} feedback item(s) and generated {rulesGenerated} parsing rule(s).";
        else
            TempData["SuccessMessage"] = "No unprocessed feedback found.";

        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TrainModel()
    {
        var result = await _invoiceService.TrainModelAsync();

        if (result.Success)
            TempData["SuccessMessage"] = result.Message
                + (result.Accuracy.HasValue ? $" Accuracy: {result.Accuracy:P1}" : "");
        else
            TempData["ErrorMessage"] = result.Message;

        return RedirectToAction("Index");
    }

    private async Task<UploadViewModel> BuildUploadViewModelAsync()
    {
        var customers = await _invoiceService.GetCustomersAsync();
        var carriers = await _invoiceService.GetCarriersAsync();

        return new UploadViewModel
        {
            Customers = customers.Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList(),
            Carriers = carriers.Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList(),
        };
    }

    private static string FormatFieldName(string propertyName)
    {
        return propertyName switch
        {
            "InvoiceNumber" => "Invoice Number",
            "CarrierAccount" => "Account Number",
            "InvoiceDate" => "Invoice Date",
            "InvoiceStartDate" => "Statement Start Date",
            "InvoiceEndDate" => "Statement End Date",
            "InvoiceDueDate" => "Due Date",
            "BeginningBalance" => "Previous Balance",
            "Payment" => "Payments",
            "PreviousAdjustments" => "Previous Adjustments",
            "CurrentAdjustments" => "Current Adjustments",
            "CurrentCharges" => "Current Charges",
            "CurrentTax" => "Taxes/Fees",
            "EndingBalance" => "Balance Due",
            _ => propertyName,
        };
    }
}
