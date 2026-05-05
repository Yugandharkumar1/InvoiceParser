using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("customer")]
public class Customer
{
    [Key]
    [Column("customer_id")]
    public int Id { get; set; }

    [Column("customer_name")]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;
}
