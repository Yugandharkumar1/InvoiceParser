using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("LineFeedback")]
public class LineFeedback
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(2000)]
    public string RawText { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    public string NormalizedText { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string PredictedLabel { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string CorrectedLabel { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
