using InvoiceParser.Core.Services;

namespace InvoiceParser.Core.Parsing;

/// <summary>Thread-safe registry for <see cref="IVendorParsingPlugin"/> implementations.</summary>
public static class VendorParsingPluginRegistry
{
    private static readonly object Gate = new();
    private static readonly List<IVendorParsingPlugin> Plugins = new();

    public static void Register(IVendorParsingPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (Gate)
            Plugins.Add(plugin);
    }

    public static void Clear()
    {
        lock (Gate)
            Plugins.Clear();
    }

    public static void ApplyAll(string? carrierCode, string normalizedText, ParsedInvoiceResult result)
    {
        List<IVendorParsingPlugin> snapshot;
        lock (Gate)
            snapshot = Plugins.ToList();

        foreach (var p in snapshot)
        {
            if (p.AppliesTo(carrierCode))
                p.Apply(normalizedText, result);
        }
    }
}
