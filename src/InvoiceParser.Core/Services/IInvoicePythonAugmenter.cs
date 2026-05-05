namespace InvoiceParser.Core.Services;

/// <summary>
/// Optional hook for the Python hybrid parser. Default web implementation calls the FastAPI service;
/// no-op can be registered for tests or non-web hosts.
/// </summary>
public interface IInvoicePythonAugmenter
{
    Task TryAugmentParseAsync(
        byte[] fileBytes,
        string fileName,
        string? vendorKey,
        ParsePdfResult result,
        CancellationToken cancellationToken = default);
}
