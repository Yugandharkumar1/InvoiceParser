using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace InvoiceParser.Core.Services.ML;

public class ModelRetrainingService : IModelRetrainingService
{
    private readonly ITrainingDataLoader _loader;
    private readonly IInvoiceMLTrainer _trainer;
    private readonly IInvoicePredictionService _predictor;
    private readonly ILogger<ModelRetrainingService> _logger;
    private readonly string _modelsFolder;
    private readonly SemaphoreSlim _retrainLock = new(1, 1);

    public ModelRetrainingService(
        ITrainingDataLoader loader,
        IInvoiceMLTrainer trainer,
        IInvoicePredictionService predictor,
        ILogger<ModelRetrainingService> logger,
        string modelsFolder)
    {
        _loader = loader;
        _trainer = trainer;
        _predictor = predictor;
        _logger = logger;
        _modelsFolder = modelsFolder;
    }

    public async Task<LineTrainingResult> RetrainAsync()
    {
        if (!await _retrainLock.WaitAsync(0))
        {
            _logger.LogWarning("Retraining already in progress. Skipping.");
            return new LineTrainingResult
            {
                Success = false,
                Message = "Retraining already in progress.",
            };
        }

        try
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation("=== MODEL RETRAINING START ===");

            var data = _loader.LoadAll();
            if (data.Count == 0)
            {
                _logger.LogWarning("No training data available. Aborting retrain.");
                return new LineTrainingResult
                {
                    Success = false,
                    Message = "No training data found.",
                };
            }

            var result = _trainer.Train(data, _modelsFolder);

            if (result.Success)
            {
                _predictor.ReloadModel();
                _logger.LogInformation("Prediction service reloaded with new model version {Version}.", result.ModelVersion);
            }

            sw.Stop();
            _logger.LogInformation("=== MODEL RETRAINING END === Duration: {Elapsed}ms, Success: {Success}",
                sw.ElapsedMilliseconds, result.Success);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model retraining failed with exception.");
            return new LineTrainingResult
            {
                Success = false,
                Message = $"Retraining error: {ex.Message}",
            };
        }
        finally
        {
            _retrainLock.Release();
        }
    }
}
