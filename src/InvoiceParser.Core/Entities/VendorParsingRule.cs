using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceParser.Core.Entities;

[Table("VendorParsingRules")]
public class VendorParsingRule
{
    [Key]
    [Column("Id")]
    public int Id { get; set; }

    [Column("CarrierId")]
    public int CarrierId { get; set; }

    [Column("FieldName")]
    [StringLength(100)]
    public string FieldName { get; set; } = string.Empty;

    [Column("RegexPattern")]
    [StringLength(500)]
    public string RegexPattern { get; set; } = string.Empty;

    [Column("FieldType")]
    [StringLength(20)]
    public string FieldType { get; set; } = "string";

    [Column("TargetTable")]
    [StringLength(50)]
    public string TargetTable { get; set; } = "t_invoice";

    [Column("Section")]
    [StringLength(50)]
    public string? Section { get; set; }

    [Column("SortOrder")]
    public int SortOrder { get; set; }

    [Column("IsActive")]
    public bool IsActive { get; set; } = true;

    [Column("SuccessCount")]
    public int SuccessCount { get; set; }

    [Column("FailCount")]
    public int FailCount { get; set; }
}
