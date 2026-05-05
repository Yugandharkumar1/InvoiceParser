using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("t_charge")]
public class Charge
{
    [Key]
    [Column("t_charge_id")]
    public int Id { get; set; }

    [Column("t_invoice_id")]
    public int InvoiceId { get; set; }

    [Column("t_usoc_map_id")]
    public int UsocMapId { get; set; }

    [Column("charge_desc")]
    [StringLength(500)]
    public string? ChargeDescription { get; set; }

    [Column("amount", TypeName = "decimal(18,2)")]
    public decimal? Amount { get; set; }

    [Column("line")]
    [StringLength(200)]
    public string? Line { get; set; }

    [Column("location")]
    [StringLength(200)]
    public string? Location { get; set; }

    [Column("account_number")]
    [StringLength(100)]
    public string? AccountNumber { get; set; }

    [Column("carrier_id")]
    public int? CarrierId { get; set; }

    [ForeignKey(nameof(InvoiceId))]
    public Invoice? Invoice { get; set; }
}
