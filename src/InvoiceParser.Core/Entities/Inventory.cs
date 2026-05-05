using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("t_inventory")]
public class Inventory
{
    [Key]
    [Column("t_inventory_id")]
    public int Id { get; set; }

    [Column("t_invoice_id")]
    public int InvoiceId { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("reference_number")]
    [StringLength(255)]
    public string ReferenceNumber { get; set; } = string.Empty;

    [Column("service_type")]
    [StringLength(255)]
    public string? ServiceType { get; set; }

    [Column("inventory_name")]
    [StringLength(255)]
    public string? InventoryName { get; set; }

    [Column("employee_name")]
    [StringLength(255)]
    public string? EmployeeName { get; set; }

    [Column("location_name")]
    [StringLength(255)]
    public string? LocationName { get; set; }

    [ForeignKey(nameof(InvoiceId))]
    public Invoice? Invoice { get; set; }
}
