using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("t_usage")]
public class Usage
{
    [Key]
    [Column("t_usage_id")]
    public int Id { get; set; }

    [Column("t_invoice_id")]
    public int InvoiceId { get; set; }

    [Column("line_number")]
    [StringLength(20)]
    public string? LineNumber { get; set; }

    [Column("t_usoc_map_id")]
    public int? UsocMapId { get; set; }

    [Column("usoc_name")]
    [StringLength(200)]
    public string? UsocName { get; set; }

    [Column("usage_limit")]
    [StringLength(50)]
    public string? UsageLimit { get; set; }

    [Column("usage")]
    [StringLength(50)]
    public string? UsageAmount { get; set; }

    [Column("charge")]
    [StringLength(50)]
    public string? Charge { get; set; }

    [Column("usagetype")]
    [StringLength(50)]
    public string? UsageType { get; set; }

    [Column("add_dtm")]
    public DateTime AddDate { get; set; } = DateTime.Now;

    [Column("account_id")]
    public int? AccountId { get; set; }

    [Column("carrier_id")]
    public int? CarrierId { get; set; }

    [ForeignKey(nameof(InvoiceId))]
    public Invoice? Invoice { get; set; }
}
