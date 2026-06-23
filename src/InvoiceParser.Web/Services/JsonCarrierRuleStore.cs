using System.Text.Json;
using InvoiceParser.Core.Entities;
using InvoiceParser.Core.Services;

namespace InvoiceParser.Web.Services;

/// <summary>
/// Stores carrier extraction rules as JSON files under
/// <c>{ContentRoot}/RuleDefinitions/{carrierId}.rules.json</c>.
///
/// Benefits over database storage:
/// - Files travel with the application code in source control.
/// - Rules survive database resets and environment changes.
/// - Teams can review/diff/merge rule changes in Git like any config file.
/// - No migration scripts needed to add or change rules.
/// </summary>
public sealed class JsonCarrierRuleStore : ICarrierRuleStore
{
    private readonly string _baseDir;
    private readonly ILogger<JsonCarrierRuleStore> _logger;

    // One lock object per carrier file to allow concurrent access to different carriers.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _fileLocks = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public JsonCarrierRuleStore(IWebHostEnvironment env, ILogger<JsonCarrierRuleStore> logger)
    {
        _baseDir = Path.Combine(env.ContentRootPath, "RuleDefinitions");
        _logger = logger;
        Directory.CreateDirectory(_baseDir);
    }

    // ─── Public API ────────────────────────────────────────────────────────────

    public async Task<List<VendorParsingRule>> GetAllRulesForCarrierAsync(int carrierId)
        => await LoadAsync(carrierId);

    public async Task<List<VendorParsingRule>> GetActiveRulesForCarrierAsync(int carrierId)
        => (await LoadAsync(carrierId)).Where(r => r.IsActive).ToList();

    public async Task<List<VendorParsingRule>> GetAllActiveRulesAsync()
    {
        var all = new List<VendorParsingRule>();
        foreach (var file in Directory.EnumerateFiles(_baseDir, "*.rules.json"))
        {
            if (!int.TryParse(Path.GetFileName(file).Split('.')[0], out var cid)) continue;
            all.AddRange((await LoadAsync(cid)).Where(r => r.IsActive));
        }
        return all;
    }

    public async Task<VendorParsingRule?> GetRuleByIdAsync(int carrierId, int id)
    {
        var rules = await LoadAsync(carrierId);
        return rules.FirstOrDefault(r => r.Id == id);
    }

    public async Task<VendorParsingRule> AddRuleAsync(VendorParsingRule rule)
    {
        var sem = GetLock(rule.CarrierId);
        await sem.WaitAsync();
        try
        {
            var rules = await LoadRawAsync(rule.CarrierId);
            rule.Id = rules.Count == 0 ? 1 : rules.Max(r => r.Id) + 1;
            rules.Add(rule);
            await SaveRawAsync(rule.CarrierId, rules);
            _logger.LogInformation("Added rule {Id} ({Field}) to carrier {CarrierId}", rule.Id, rule.FieldName, rule.CarrierId);
            return rule;
        }
        finally { sem.Release(); }
    }

    public async Task UpdateRuleAsync(VendorParsingRule rule)
    {
        var sem = GetLock(rule.CarrierId);
        await sem.WaitAsync();
        try
        {
            var rules = await LoadRawAsync(rule.CarrierId);
            var idx = rules.FindIndex(r => r.Id == rule.Id);
            if (idx < 0)
            {
                _logger.LogWarning("UpdateRuleAsync: rule {Id} not found in carrier {CarrierId}", rule.Id, rule.CarrierId);
                return;
            }
            rules[idx] = rule;
            await SaveRawAsync(rule.CarrierId, rules);
        }
        finally { sem.Release(); }
    }

    public async Task DeleteRuleAsync(int carrierId, int id)
    {
        var sem = GetLock(carrierId);
        await sem.WaitAsync();
        try
        {
            var rules = await LoadRawAsync(carrierId);
            var removed = rules.RemoveAll(r => r.Id == id);
            if (removed > 0)
            {
                await SaveRawAsync(carrierId, rules);
                _logger.LogInformation("Deleted rule {Id} from carrier {CarrierId}", id, carrierId);
            }
            else
            {
                _logger.LogWarning("DeleteRuleAsync: rule {Id} not found in carrier {CarrierId}", id, carrierId);
            }
        }
        finally { sem.Release(); }
    }

    // ─── Detection keywords ────────────────────────────────────────────────────

    private string KeywordsFilePath(int carrierId) => Path.Combine(_baseDir, $"{carrierId}.detection.json");

    public async Task<List<string>> GetDetectionKeywordsAsync(int carrierId)
    {
        var path = KeywordsFilePath(carrierId);
        if (!File.Exists(path)) return new List<string>();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<string>>(json, _jsonOpts) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read detection keywords file {Path}", path);
            return new List<string>();
        }
    }

    public async Task SaveDetectionKeywordsAsync(int carrierId, List<string> keywords)
    {
        var sem = GetLock(carrierId);
        await sem.WaitAsync();
        try
        {
            var path = KeywordsFilePath(carrierId);
            var clean = keywords
                .Select(k => k.Trim())
                .Where(k => k.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k)
                .ToList();
            var json = JsonSerializer.Serialize(clean, _jsonOpts);
            await File.WriteAllTextAsync(path, json);
            _logger.LogInformation("Saved {Count} detection keywords for carrier {CarrierId}", clean.Count, carrierId);
        }
        finally { sem.Release(); }
    }

    // ─── Skipped summary fields ────────────────────────────────────────────────

    private string SkippedFieldsFilePath(int carrierId) => Path.Combine(_baseDir, $"{carrierId}.skipped.json");

    public async Task<List<string>> GetSkippedSummaryFieldsAsync(int carrierId)
    {
        var path = SkippedFieldsFilePath(carrierId);
        if (!File.Exists(path)) return new List<string>();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<string>>(json, _jsonOpts) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read skipped fields file {Path}", path);
            return new List<string>();
        }
    }

    public async Task AddSkippedSummaryFieldsAsync(int carrierId, IEnumerable<string> fieldNames)
    {
        var sem = GetLock(carrierId);
        await sem.WaitAsync();
        try
        {
            var path = SkippedFieldsFilePath(carrierId);
            var existing = new List<string>();
            if (File.Exists(path))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(path);
                    existing = JsonSerializer.Deserialize<List<string>>(raw, _jsonOpts) ?? new List<string>();
                }
                catch { /* start fresh */ }
            }

            var merged = existing
                .Concat(fieldNames.Select(f => f.Trim()).Where(f => f.Length > 0))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f)
                .ToList();

            var json = JsonSerializer.Serialize(merged, _jsonOpts);
            await File.WriteAllTextAsync(path, json);
            _logger.LogInformation("Saved skipped summary fields for carrier {CarrierId}: {Fields}", carrierId, string.Join(", ", merged));
        }
        finally { sem.Release(); }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private string FilePath(int carrierId) => Path.Combine(_baseDir, $"{carrierId}.rules.json");

    private SemaphoreSlim GetLock(int carrierId)
        => _fileLocks.GetOrAdd(carrierId.ToString(), _ => new SemaphoreSlim(1, 1));

    /// <summary>Load without acquiring lock (caller is responsible for locking when writing).</summary>
    private async Task<List<VendorParsingRule>> LoadRawAsync(int carrierId)
    {
        var path = FilePath(carrierId);
        if (!File.Exists(path)) return new List<VendorParsingRule>();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<VendorParsingRule>>(json, _jsonOpts)
                   ?? new List<VendorParsingRule>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read rule file {Path}", path);
            return new List<VendorParsingRule>();
        }
    }

    /// <summary>Public read — acquires the per-carrier lock for consistency.</summary>
    private async Task<List<VendorParsingRule>> LoadAsync(int carrierId)
    {
        var sem = GetLock(carrierId);
        await sem.WaitAsync();
        try { return await LoadRawAsync(carrierId); }
        finally { sem.Release(); }
    }

    private async Task SaveRawAsync(int carrierId, List<VendorParsingRule> rules)
    {
        var path = FilePath(carrierId);
        var json = JsonSerializer.Serialize(rules, _jsonOpts);
        await File.WriteAllTextAsync(path, json);
    }
}
