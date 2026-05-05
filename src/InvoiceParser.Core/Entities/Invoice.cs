using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("t_invoice")]
public class Invoice
{
    [Key]
    [Column("t_invoice_id")]
    public int Id { get; set; }

    [Column("customer_id")]
    public int? CustomerId { get; set; }

    [Column("carrier_id")]
    public int? CarrierId { get; set; }

    [Column("carrier_code")]
    [StringLength(50)]
    public string? CarrierCode { get; set; }

    [Column("carrier_name")]
    [StringLength(200)]
    public string? CarrierName { get; set; }

    [Column("carrier_account")]
    [StringLength(100)]
    public string? CarrierAccount { get; set; }

    [Column("invoice_number")]
    [StringLength(100)]
    public string? InvoiceNumber { get; set; }

    [Column("invoice_date")]
    public DateTime? InvoiceDate { get; set; }

    [Column("invoice_st_dtm")]
    public DateTime? InvoiceStartDate { get; set; }

    [Column("invoice_end_dtm")]
    public DateTime? InvoiceEndDate { get; set; }

    [Column("invoice_due_dtm")]
    public DateTime? InvoiceDueDate { get; set; }

    [Column("beg_bal", TypeName = "decimal(18,2)")]
    public decimal? BeginningBalance { get; set; }

    [Column("payment", TypeName = "decimal(18,2)")]
    public decimal? Payment { get; set; }

    [Column("prev_adj", TypeName = "decimal(18,2)")]
    public decimal? PreviousAdjustments { get; set; }

    [Column("curr_adj", TypeName = "decimal(18,2)")]
    public decimal? CurrentAdjustments { get; set; }

    [Column("curr_chg", TypeName = "decimal(18,2)")]
    public decimal? CurrentCharges { get; set; }

    [Column("curr_tax", TypeName = "decimal(18,2)")]
    public decimal? CurrentTax { get; set; }

    [Column("end_bal", TypeName = "decimal(18,2)")]
    public decimal? EndingBalance { get; set; }

    [Column("is_summary")]
    public bool IsSummary { get; set; }

    [Column("email_sent")]
    public bool EmailSent { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("add_usr")]
    [StringLength(100)]
    public string AddUser { get; set; } = "InvoiceParser";

    [Column("add_dtm")]
    public DateTime AddDate { get; set; } = DateTime.Now;

    [Column("pdf_text")]
    public string? PdfText { get; set; }

    public ICollection<Charge> Charges { get; set; } = new List<Charge>();
}
