using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("InvoiceFeedback")]
public class InvoiceFeedback
{
    [Key]
    [Column("Id")]
    public int Id { get; set; }

    [Column("CarrierId")]
    public int CarrierId { get; set; }

    [Column("FeedbackText")]
    public string FeedbackText { get; set; } = string.Empty;

    [Column("PdfText")]
    public string? PdfText { get; set; }

    [Column("OriginalFieldsJson")]
    public string? OriginalFieldsJson { get; set; }

    [Column("ConfirmedFieldsJson")]
    public string? ConfirmedFieldsJson { get; set; }

    [Column("OriginalChargesJson")]
    public string? OriginalChargesJson { get; set; }

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("IsProcessed")]
    public bool IsProcessed { get; set; }
}
