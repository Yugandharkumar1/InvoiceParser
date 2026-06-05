using InvoiceParser.Core.Entities;

namespace InvoiceParser.Web.Models;

public class RulesSectionViewModel
{
    public string SectionTitle   { get; set; } = string.Empty;
    public string SectionDesc    { get; set; } = string.Empty;
    /// Bootstrap badge class for the rule-count pill, e.g. "bg-primary"
    public string BadgeClass     { get; set; } = "bg-secondary";
    /// t_invoice | t_charge | t_usage | t_inventory
    public string Section        { get; set; } = string.Empty;
    public int    CarrierId      { get; set; }
    public List<VendorParsingRule> Rules { get; set; } = new();
    /// Show a Delete button (summary rules are never standalone-deletable from the index)
    public bool   ShowDeleteBtn  { get; set; } = true;
}
