using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services.ML;

public class TrainingDataLoader : ITrainingDataLoader
{
    private static readonly HashSet<string> ValidLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Summary", "Charges", "Ignore"
    };

    private readonly string _folderPath;
    private readonly ILogger<TrainingDataLoader> _logger;

    public TrainingDataLoader(string folderPath, ILogger<TrainingDataLoader> logger)
    {
        _folderPath = folderPath;
        _logger = logger;
    }

    public List<LineClassificationData> LoadAll()
    {
        var allData = new List<LineClassificationData>();

        if (!Directory.Exists(_folderPath))
        {
            _logger.LogWarning("Training data folder does not exist: {Path}", _folderPath);
            return allData;
        }

        var csvFiles = Directory.GetFiles(_folderPath, "*.csv");
        if (csvFiles.Length == 0)
        {
            _logger.LogInformation("No CSV files found in {Path}", _folderPath);
            return allData;
        }

        _logger.LogInformation("Loading training data from {Count} CSV file(s) in {Path}", csvFiles.Length, _folderPath);

        foreach (var file in csvFiles)
        {
            try
            {
                var rows = LoadCsvFile(file);
                allData.AddRange(rows);
                _logger.LogDebug("Loaded {Count} rows from {File}", rows.Count, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load CSV file: {File}", file);
            }
        }

        var labelCounts = allData.GroupBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogInformation(
            "Training data loaded: {Total} rows. Distribution: {Distribution}",
            allData.Count,
            string.Join(", ", labelCounts.Select(kv => $"{kv.Key}={kv.Value}")));

        return allData;
    }

    private List<LineClassificationData> LoadCsvFile(string filePath)
    {
        var results = new List<LineClassificationData>();
        var lines = File.ReadAllLines(filePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Skip header row
            if (i == 0 && line.StartsWith("Text,", StringComparison.OrdinalIgnoreCase))
                continue;

            var lastComma = line.LastIndexOf(',');
            if (lastComma <= 0 || lastComma >= line.Length - 1)
            {
                _logger.LogWarning("Skipping malformed row {LineNum} in {File}: {Line}",
                    i + 1, Path.GetFileName(filePath), line);
                continue;
            }

            var text = line[..lastComma].Trim().Trim('"');
            var label = line[(lastComma + 1)..].Trim().Trim('"');

            if (!ValidLabels.Contains(label))
            {
                _logger.LogWarning(
                    "Skipping row {LineNum} in {File}: invalid label '{Label}'. Expected: Summary, Charges, or Ignore",
                    i + 1, Path.GetFileName(filePath), label);
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            results.Add(new LineClassificationData { Text = text, Label = label });
        }

        return results;
    }
}
