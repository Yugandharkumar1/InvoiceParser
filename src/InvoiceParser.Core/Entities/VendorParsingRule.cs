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

    /// <summary>
    /// How the condition is evaluated against the source.
    /// Values: regex_match (default/legacy), equals, not_equals, contains, does_not_contain,
    ///         starts_with, ends_with, is_empty, is_not_empty, greater_than, less_than.
    /// </summary>
    [Column("ConditionType")]
    [StringLength(30)]
    public string ConditionType { get; set; } = "regex_match";

    /// <summary>
    /// What is tested by the condition.
    /// Values: line_text (default), amount, field_value.
    /// </summary>
    [Column("ConditionSource")]
    [StringLength(30)]
    public string ConditionSource { get; set; } = "line_text";

    /// <summary>
    /// JSON-serialised <c>List&lt;TransformStep&gt;</c> applied to the extracted value before storing.
    /// Null means no transformation.
    /// </summary>
    [Column("TransformationsJson")]
    public string? TransformationsJson { get; set; }
}
