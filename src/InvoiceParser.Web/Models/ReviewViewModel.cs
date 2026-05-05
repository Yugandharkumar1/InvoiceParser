namespace InvoiceParser.Web.Models;

public class ReviewViewModel
{
    public int CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public int CarrierId { get; set; }
    public string? CarrierName { get; set; }
    public string? CarrierCode { get; set; }

    public string? InvoiceNumber { get; set; }
    public string? CarrierAccount { get; set; }
    public string? InvoiceDate { get; set; }
    public string? InvoiceStartDate { get; set; }
    public string? InvoiceEndDate { get; set; }
    public string? InvoiceDueDate { get; set; }

    public string? BeginningBalance { get; set; }
    public string? Payment { get; set; }
    public string? PreviousAdjustments { get; set; }
    public string? CurrentAdjustments { get; set; }
    public string? CurrentCharges { get; set; }
    public string? CurrentTax { get; set; }
    public string? EndingBalance { get; set; }

    public string? PdfText { get; set; }

    /// <summary>Python FastAPI parse id (for optional feedback POST).</summary>
    public string? PythonParseId { get; set; }

    /// <summary>Serialized OCR payload from Python (for /feedback training).</summary>
    public string? PythonOcrJson { get; set; }

    public string? PythonSource { get; set; }
    public string? PythonVendorHint { get; set; }

    // Original values from parser for feedback comparison
    public string? Orig_InvoiceNumber { get; set; }
    public string? Orig_CarrierAccount { get; set; }
    public string? Orig_InvoiceDate { get; set; }
    public string? Orig_InvoiceStartDate { get; set; }
    public string? Orig_InvoiceEndDate { get; set; }
    public string? Orig_InvoiceDueDate { get; set; }
    public string? Orig_BeginningBalance { get; set; }
    public string? Orig_Payment { get; set; }
    public string? Orig_PreviousAdjustments { get; set; }
    public string? Orig_CurrentAdjustments { get; set; }
    public string? Orig_CurrentCharges { get; set; }
    public string? Orig_CurrentTax { get; set; }
    public string? Orig_EndingBalance { get; set; }
    public int Orig_ChargeCount { get; set; }

    /// <summary>JSON-serialized list of original charge descriptions/amounts from parser for feedback diff.</summary>
    public string? OriginalChargesJson { get; set; }

    public List<ReviewChargeItem> Charges { get; set; } = new();
    public List<ReviewUsageItem> Usages { get; set; } = new();
    public List<ReviewInventoryItem> Inventories { get; set; } = new();
}

public class ReviewChargeItem
{
    public string? ChargeDescription { get; set; }
    public string? Amount { get; set; }
    public string? Line { get; set; }
    public string? Location { get; set; }
}

public class ReviewUsageItem
{
    public string? EmployeeName { get; set; }
    public string? LineNumber { get; set; }
    public string? UsageType { get; set; }
    public string? UsocName { get; set; }
    public string? UsageLimit { get; set; }
    public string? UsageAmount { get; set; }
    public string? Charge { get; set; }
}

public class ReviewInventoryItem
{
    public string? EmployeeName { get; set; }
    public string? LineNumber { get; set; }
    public string? PlanName { get; set; }
    public string? PlanAmount { get; set; }
    public string? ServiceType { get; set; }
}

public class OriginalChargeItem
{
    public string? Description { get; set; }
    public string? Amount { get; set; }
}

