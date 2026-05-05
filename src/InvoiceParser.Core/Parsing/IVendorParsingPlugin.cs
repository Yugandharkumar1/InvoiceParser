using InvoiceParser.Core.Services;

namespace InvoiceParser.Core.Parsing;

/// <summary>
/// Optional vendor-specific adjustments after baseline SmartParse. Register via <see cref="VendorParsingPluginRegistry"/>.
/// </summary>
public interface IVendorParsingPlugin
{
    /// <summary>Carrier code from DB (e.g. Verizon); null means plugin decides internally.</summary>
    bool AppliesTo(string? carrierCode);

    /// <summary>Mutate <paramref name="result"/> in place (typically add patterns or tweak fields).</summary>
    void Apply(string normalizedText, ParsedInvoiceResult result);
}
