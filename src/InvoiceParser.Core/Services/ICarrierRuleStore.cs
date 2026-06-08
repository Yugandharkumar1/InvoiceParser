using InvoiceParser.Core.Entities;

namespace InvoiceParser.Core.Services;

/// <summary>
/// Abstracts where carrier extraction rules are physically stored.
/// The default implementation (<c>JsonCarrierRuleStore</c>) persists rules as JSON files
/// alongside the application — meaning they are version-controlled, deployment-portable,
/// and never lost when the database is swapped between environments.
/// </summary>
public interface ICarrierRuleStore
{
    /// <summary>All rules (active and inactive) for a single carrier.</summary>
    Task<List<VendorParsingRule>> GetAllRulesForCarrierAsync(int carrierId);

    /// <summary>Active rules only for a single carrier — used during invoice parsing.</summary>
    Task<List<VendorParsingRule>> GetActiveRulesForCarrierAsync(int carrierId);

    /// <summary>All active rules across every carrier — used to generate ML training samples.</summary>
    Task<List<VendorParsingRule>> GetAllActiveRulesAsync();

    /// <summary>Finds a single rule by its ID (scans all carriers).</summary>
    Task<VendorParsingRule?> GetRuleByIdAsync(int id);

    /// <summary>Inserts a new rule and returns it with its newly assigned ID.</summary>
    Task<VendorParsingRule> AddRuleAsync(VendorParsingRule rule);

    /// <summary>Persists changes to an existing rule.</summary>
    Task UpdateRuleAsync(VendorParsingRule rule);

    /// <summary>Permanently removes a rule by ID.</summary>
    Task DeleteRuleAsync(int id);
}
