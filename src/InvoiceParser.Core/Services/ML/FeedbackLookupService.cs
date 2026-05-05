using InvoiceParser.Core.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services.ML;

public class FeedbackLookupService : IFeedbackLookupService
{
    private const double FuzzyThreshold = 0.8;

    private readonly ILogger<FeedbackLookupService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReaderWriterLockSlim _rwLock = new();

    private Dictionary<string, LineFeedback> _exactCache = new(StringComparer.OrdinalIgnoreCase);
    private List<LineFeedback> _allEntries = new();
    private bool _isLoaded;

    public FeedbackLookupService(ILogger<FeedbackLookupService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public FeedbackMatchResult Lookup(string text)
    {
        EnsureCacheLoaded();

        var normalized = TextNormalizer.NormalizeText(text);
        if (string.IsNullOrEmpty(normalized))
            return new FeedbackMatchResult { IsMatch = false };

        _rwLock.EnterReadLock();
        try
        {
            if (_exactCache.TryGetValue(normalized, out var exactMatch))
            {
                _logger.LogInformation("Feedback exact match: '{Text}' -> {Label}",
                    text.Length > 60 ? text[..60] + "..." : text, exactMatch.CorrectedLabel);

                return new FeedbackMatchResult
                {
                    IsMatch = true,
                    CorrectedLabel = exactMatch.CorrectedLabel,
                    Confidence = 1.0,
                };
            }

            LineFeedback? bestMatch = null;
            double bestSimilarity = 0;

            foreach (var entry in _allEntries)
            {
                if (entry.NormalizedText.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains(entry.NormalizedText, StringComparison.OrdinalIgnoreCase))
                {
                    var sim = TextNormalizer.LevenshteinSimilarity(normalized, entry.NormalizedText);
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        bestMatch = entry;
                    }
                    continue;
                }

                var similarity = TextNormalizer.LevenshteinSimilarity(normalized, entry.NormalizedText);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestMatch = entry;
                }
            }

            if (bestMatch != null && bestSimilarity >= FuzzyThreshold)
            {
                _logger.LogInformation("Feedback fuzzy match ({Similarity:F2}): '{Text}' -> {Label}",
                    bestSimilarity, text.Length > 60 ? text[..60] + "..." : text, bestMatch.CorrectedLabel);

                return new FeedbackMatchResult
                {
                    IsMatch = true,
                    CorrectedLabel = bestMatch.CorrectedLabel,
                    Confidence = bestSimilarity,
                };
            }

            return new FeedbackMatchResult { IsMatch = false };
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public async Task RefreshCacheAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
            var entries = await repository.GetAllLineFeedbackAsync();

            var newExactCache = new Dictionary<string, LineFeedback>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                newExactCache[entry.NormalizedText] = entry;
            }

            _rwLock.EnterWriteLock();
            try
            {
                _exactCache = newExactCache;
                _allEntries = entries;
                _isLoaded = true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            _logger.LogInformation("Feedback lookup cache refreshed with {Count} entries.", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh feedback lookup cache.");
        }
    }

    public async Task SaveLineFeedbackAsync(string rawText, string predictedLabel, string correctedLabel)
    {
        var normalized = TextNormalizer.NormalizeText(rawText);
        if (string.IsNullOrEmpty(normalized))
            return;

        var feedback = new LineFeedback
        {
            RawText = rawText.Length > 2000 ? rawText[..2000] : rawText,
            NormalizedText = normalized.Length > 2000 ? normalized[..2000] : normalized,
            PredictedLabel = predictedLabel,
            CorrectedLabel = correctedLabel,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
            await repository.SaveLineFeedbackAsync(feedback);

            _logger.LogInformation("Saved line feedback: '{Text}' predicted={Predicted} corrected={Corrected}",
                rawText.Length > 60 ? rawText[..60] + "..." : rawText, predictedLabel, correctedLabel);

            await RefreshCacheAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save line feedback.");
        }
    }

    private void EnsureCacheLoaded()
    {
        if (_isLoaded) return;

        _rwLock.EnterReadLock();
        try
        {
            if (_isLoaded) return;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }

        RefreshCacheAsync().GetAwaiter().GetResult();
    }
}
