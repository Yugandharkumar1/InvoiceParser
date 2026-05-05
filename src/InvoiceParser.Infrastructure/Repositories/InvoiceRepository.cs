using System.Text.Json;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services;
using InvoiceParser.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceParser.Infrastructure.Repositories;

public class InvoiceRepository : IInvoiceRepository
{
    private readonly AppDbContext _db;
    private readonly IPathDbContext _iPathDb;

    public InvoiceRepository(AppDbContext db, IPathDbContext iPathDb)
    {
        _db = db;
        _iPathDb = iPathDb;
    }

    public async Task<List<Customer>> GetCustomersAsync()
        => await _iPathDb.Customers.OrderBy(c => c.Name).ToListAsync();

    public async Task<List<Carrier>> GetCarriersAsync()
        => await _iPathDb.Carriers.OrderBy(c => c.Name).ToListAsync();

    public async Task<Carrier?> GetCarrierByIdAsync(int id)
        => await _iPathDb.Carriers.FindAsync(id);

    public async Task<List<VendorParsingRule>> GetRulesForCarrierAsync(int carrierId)
        => await _db.VendorParsingRules
            .Where(r => r.CarrierId == carrierId && r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ToListAsync();

    public async Task<Invoice> SaveInvoiceAsync(Invoice invoice)
    {
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
        return invoice;
    }

    public async Task<Invoice?> GetInvoiceByIdAsync(int id)
        => await _db.Invoices
            .Include(i => i.Charges)
            .FirstOrDefaultAsync(i => i.Id == id);

    public async Task<List<Invoice>> GetAllInvoicesAsync()
        => await _db.Invoices
            .OrderByDescending(i => i.InvoiceDate)
            .ToListAsync();

    public async Task<List<Invoice>> GetInvoicesWithPdfTextAsync()
        => await _db.Invoices
            .Include(i => i.Charges)
            .Where(i => i.PdfText != null && i.PdfText != "")
            .ToListAsync();

    public async Task<int> GetInvoiceCountWithPdfTextAsync()
        => await _db.Invoices
            .CountAsync(i => i.PdfText != null && i.PdfText != "");

    public async Task UpdateRuleFeedbackAsync(int carrierId, List<string> correctFields, List<string> correctedFields)
    {
        var rules = await _db.VendorParsingRules
            .Where(r => r.CarrierId == carrierId && r.IsActive && r.TargetTable == "t_invoice")
            .ToListAsync();

        foreach (var rule in rules)
        {
            if (correctFields.Contains(rule.FieldName))
                rule.SuccessCount++;
            else if (correctedFields.Contains(rule.FieldName))
                rule.FailCount++;
        }

        await _db.SaveChangesAsync();
    }

    public async Task SaveLearnedRulesAsync(int carrierId, List<VendorParsingRule> rules)
    {
        var targetTables = rules.Select(r => r.TargetTable).Distinct().ToList();
        var existingRules = await _db.VendorParsingRules
            .Where(r => r.CarrierId == carrierId && targetTables.Contains(r.TargetTable))
            .ToListAsync();

        foreach (var newRule in rules)
        {
            var existing = existingRules.FirstOrDefault(r =>
                r.FieldName == newRule.FieldName &&
                r.TargetTable == newRule.TargetTable &&
                r.RegexPattern == newRule.RegexPattern);

            if (existing != null)
            {
                existing.SuccessCount++;
            }
            else
            {
                var sameField = existingRules.FirstOrDefault(r =>
                    r.FieldName == newRule.FieldName &&
                    r.TargetTable == newRule.TargetTable &&
                    r.FieldType != "skip");

                if (sameField != null && newRule.FieldType != "skip")
                {
                    sameField.RegexPattern = newRule.RegexPattern;
                    sameField.SuccessCount = 1;
                    sameField.FailCount = 0;
                    sameField.IsActive = true;
                }
                else
                {
                    _db.VendorParsingRules.Add(newRule);
                }
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task SaveFeedbackAsync(InvoiceFeedback feedback)
    {
        _db.InvoiceFeedbacks.Add(feedback);
        await _db.SaveChangesAsync();
    }

    public async Task<InvoiceFeedback?> GetLatestFeedbackByCarrierAccountAndInvoiceDateAsync(
        int carrierId, string normalizedAccount, string normalizedInvoiceDate)
    {
        if (string.IsNullOrEmpty(normalizedAccount) || string.IsNullOrEmpty(normalizedInvoiceDate))
            return null;

        const int maxRows = 800;
        var candidates = await _db.InvoiceFeedbacks
            .AsNoTracking()
            .Where(f => f.CarrierId == carrierId && f.ConfirmedFieldsJson != null && f.ConfirmedFieldsJson != "")
            .OrderByDescending(f => f.CreatedAt)
            .Take(maxRows)
            .ToListAsync();

        foreach (var fb in candidates)
        {
            Dictionary<string, string?>? confirmed;
            try
            {
                confirmed = JsonSerializer.Deserialize<Dictionary<string, string?>>(fb.ConfirmedFieldsJson!);
            }
            catch
            {
                continue;
            }

            if (confirmed == null) continue;
            confirmed.TryGetValue("carrier_account", out var acctRaw);
            confirmed.TryGetValue("invoice_date", out var dateRaw);
            var acctKey = FeedbackFieldNormalizer.NormalizeAccountKey(acctRaw);
            if (!FeedbackFieldNormalizer.TryNormalizeInvoiceDateKey(dateRaw, out var dateKey))
                continue;
            if (!acctKey.Equals(normalizedAccount, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!dateKey.Equals(normalizedInvoiceDate, StringComparison.OrdinalIgnoreCase))
                continue;

            return fb;
        }

        return null;
    }

    public async Task<List<InvoiceFeedback>> GetUnprocessedFeedbackAsync()
        => await _db.InvoiceFeedbacks
            .Where(f => !f.IsProcessed)
            .OrderBy(f => f.CreatedAt)
            .ToListAsync();

    public async Task MarkFeedbackProcessedAsync(int feedbackId)
    {
        var feedback = await _db.InvoiceFeedbacks.FindAsync(feedbackId);
        if (feedback != null)
        {
            feedback.IsProcessed = true;
            await _db.SaveChangesAsync();
        }
    }

    public async Task MarkFeedbackBatchProcessedAsync(List<int> feedbackIds)
    {
        if (feedbackIds.Count == 0) return;

        await _db.InvoiceFeedbacks
            .Where(f => feedbackIds.Contains(f.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsProcessed, true));
    }

    public async Task SaveUsagesAsync(List<Usage> usages)
    {
        if (usages.Count == 0) return;
        _db.Usages.AddRange(usages);
        await _db.SaveChangesAsync();
    }

    public async Task SaveInventoriesAsync(List<Inventory> inventories)
    {
        if (inventories.Count == 0) return;
        _db.Inventories.AddRange(inventories);
        await _db.SaveChangesAsync();
    }

    public async Task<Invoice?> FindDuplicateInvoiceAsync(string? invoiceNumber, int? carrierId)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber)) return null;

        return await _db.Invoices.FirstOrDefaultAsync(i =>
            i.InvoiceNumber == invoiceNumber && i.CarrierId == carrierId);
    }

    public async Task SaveInvoiceWithRelatedDataAsync(Invoice invoice, List<Usage> usages, List<Inventory> inventories)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            _db.Invoices.Add(invoice);
            await _db.SaveChangesAsync();

            if (usages.Count > 0)
            {
                foreach (var u in usages) u.InvoiceId = invoice.Id;
                _db.Usages.AddRange(usages);
            }

            if (inventories.Count > 0)
            {
                foreach (var inv in inventories) inv.InvoiceId = invoice.Id;
                _db.Inventories.AddRange(inventories);
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<LineFeedback>> GetAllLineFeedbackAsync()
        => await _db.LineFeedbacks
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();

    public async Task SaveLineFeedbackAsync(LineFeedback feedback)
    {
        _db.LineFeedbacks.Add(feedback);
        await _db.SaveChangesAsync();
    }
}
