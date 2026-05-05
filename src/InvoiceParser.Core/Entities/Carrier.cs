using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("carrier")]
public class Carrier
{
    [Key]
    [Column("carrier_id")]
    public int Id { get; set; }

    [Column("carrier_cd")]
    [StringLength(10)]
    public string Code { get; set; } = string.Empty;

    [Column("carrier_desc")]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;
}
