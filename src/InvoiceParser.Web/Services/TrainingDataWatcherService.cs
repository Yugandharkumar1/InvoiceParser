using InvoiceParser.Core.Services.ML;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Web.Services;

public class TrainingDataWatcherService : IHostedService, IDisposable
{
    private readonly IModelRetrainingService _retrainingService;
    private readonly ILogger<TrainingDataWatcherService> _logger;
    private readonly string _trainingDataPath;
    private readonly string _modelsPath;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _timerLock = new();
    private const int DebounceDelayMs = 5000;

    public TrainingDataWatcherService(
        IModelRetrainingService retrainingService,
        ILogger<TrainingDataWatcherService> logger,
        IHostEnvironment environment)
    {
        _retrainingService = retrainingService;
        _logger = logger;
        _trainingDataPath = Path.Combine(environment.ContentRootPath, "TrainingData");
        _modelsPath = Path.Combine(environment.ContentRootPath, "Models");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureDirectories();

        _watcher = new FileSystemWatcher(_trainingDataPath, "*.csv")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Created += OnCsvChanged;
        _watcher.Changed += OnCsvChanged;
        _watcher.Renamed += OnCsvRenamed;

        _logger.LogInformation("Training data watcher started. Monitoring: {Path}", _trainingDataPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Training data watcher stopping.");

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCsvChanged;
            _watcher.Changed -= OnCsvChanged;
            _watcher.Renamed -= OnCsvRenamed;
        }

        lock (_timerLock)
        {
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        return Task.CompletedTask;
    }

    private void OnCsvChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("CSV file {Event}: {File}. Scheduling retrain in {Delay}ms.",
            e.ChangeType, e.Name, DebounceDelayMs);
        ScheduleRetrain();
    }

    private void OnCsvRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogInformation("CSV file renamed: {Old} -> {New}. Scheduling retrain in {Delay}ms.",
            e.OldName, e.Name, DebounceDelayMs);
        ScheduleRetrain();
    }

    private void ScheduleRetrain()
    {
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, DebounceDelayMs, Timeout.Infinite);
        }
    }

    private async void OnDebounceElapsed(object? state)
    {
        try
        {
            _logger.LogInformation("Debounce elapsed. Triggering automatic retraining...");
            var result = await _retrainingService.RetrainAsync();

            if (result.Success)
                _logger.LogInformation("Auto-retrain succeeded: {Message}", result.Message);
            else
                _logger.LogWarning("Auto-retrain did not succeed: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-retrain failed with exception.");
        }
    }

    private void EnsureDirectories()
    {
        if (!Directory.Exists(_trainingDataPath))
        {
            Directory.CreateDirectory(_trainingDataPath);
            _logger.LogInformation("Created TrainingData directory: {Path}", _trainingDataPath);
        }

        if (!Directory.Exists(_modelsPath))
        {
            Directory.CreateDirectory(_modelsPath);
            _logger.LogInformation("Created Models directory: {Path}", _modelsPath);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
