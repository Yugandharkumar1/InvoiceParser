using InvoiceParser.Core.Entities;

namespace InvoiceParser.Core.Services;

public interface IInvoiceRepository
{
    Task<List<Customer>> GetCustomersAsync();
    Task<List<Carrier>> GetCarriersAsync();
    Task<Carrier?> GetCarrierByIdAsync(int id);
    Task<List<VendorParsingRule>> GetRulesForCarrierAsync(int carrierId);
    Task<Invoice> SaveInvoiceAsync(Invoice invoice);
    Task<Invoice?> GetInvoiceByIdAsync(int id);
    Task<List<Invoice>> GetAllInvoicesAsync();
    Task SaveLearnedRulesAsync(int carrierId, List<VendorParsingRule> rules);
    Task<List<Invoice>> GetInvoicesWithPdfTextAsync();
    Task<int> GetInvoiceCountWithPdfTextAsync();
    Task UpdateRuleFeedbackAsync(int carrierId, List<string> correctFields, List<string> correctedFields);
    Task SaveFeedbackAsync(InvoiceFeedback feedback);
    /// <summary>Latest row where confirmed carrier_account + invoice_date match (normalized). Includes processed feedback.</summary>
    Task<InvoiceFeedback?> GetLatestFeedbackByCarrierAccountAndInvoiceDateAsync(
        int carrierId, string normalizedAccount, string normalizedInvoiceDate);
    Task<List<InvoiceFeedback>> GetUnprocessedFeedbackAsync();
    Task MarkFeedbackProcessedAsync(int feedbackId);
    Task MarkFeedbackBatchProcessedAsync(List<int> feedbackIds);
    Task SaveUsagesAsync(List<Usage> usages);
    Task SaveInventoriesAsync(List<Inventory> inventories);
    Task<Invoice?> FindDuplicateInvoiceAsync(string? invoiceNumber, int? carrierId);
    Task SaveInvoiceWithRelatedDataAsync(Invoice invoice, List<Usage> usages, List<Inventory> inventories);
    Task<List<LineFeedback>> GetAllLineFeedbackAsync();
    Task SaveLineFeedbackAsync(LineFeedback feedback);
}
