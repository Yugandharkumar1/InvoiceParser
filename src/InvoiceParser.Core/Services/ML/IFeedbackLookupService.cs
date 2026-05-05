namespace InvoiceParser.Core.Services.ML;

public interface IFeedbackLookupService
{
    FeedbackMatchResult Lookup(string text);
    Task RefreshCacheAsync();
    Task SaveLineFeedbackAsync(string rawText, string predictedLabel, string correctedLabel);
}
