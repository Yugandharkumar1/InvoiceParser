namespace InvoiceParser.Core.Services;

/// <summary>
/// A single transformation step applied to an extracted value.
/// Steps are chained in order, each receiving the output of the previous step.
/// </summary>
public class TransformStep
{
    /// <summary>
    /// Transformation type. Supported values:
    /// remove_prefix, remove_suffix, replace, trim,
    /// extract_between, regex_extract, convert_date,
    /// to_upper, to_lower.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// First parameter.
    /// remove_prefix  → the prefix string to remove
    /// remove_suffix  → the suffix string to remove
    /// replace        → the old text to find
    /// extract_between → the start marker
    /// regex_extract  → the regex pattern (capture group 1 is extracted)
    /// convert_date   → the input date format (e.g. "MM/dd/yy", "MMMM d, yyyy")
    /// </summary>
    public string? Value1 { get; set; }

    /// <summary>
    /// Second parameter.
    /// replace        → the replacement text (empty = delete)
    /// extract_between → the end marker
    /// convert_date   → the output date format (e.g. "yyyy-MM-dd")
    /// </summary>
    public string? Value2 { get; set; }
}
