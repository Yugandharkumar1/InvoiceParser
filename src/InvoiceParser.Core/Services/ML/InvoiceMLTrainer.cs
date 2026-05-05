using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML;

namespace InvoiceParser.Core.Services.ML;

public class InvoiceMLTrainer : IInvoiceMLTrainer
{
    private const int MinimumSamples = 20;

    private readonly MLContext _mlContext;
    private readonly ILogger<InvoiceMLTrainer> _logger;

    public InvoiceMLTrainer(ILogger<InvoiceMLTrainer> logger)
    {
        _mlContext = new MLContext(seed: 42);
        _logger = logger;
    }

    public LineTrainingResult Train(List<LineClassificationData> data, string modelsFolder)
    {
        if (data.Count < MinimumSamples)
        {
            var msg = $"Not enough training samples ({data.Count}). Need at least {MinimumSamples}.";
            _logger.LogWarning(msg);
            return new LineTrainingResult { Success = false, Message = msg };
        }

        try
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation("Starting line classification training with {Count} samples...", data.Count);

            var dataView = _mlContext.Data.LoadFromEnumerable(data);

            var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label")
                .Append(_mlContext.Transforms.Text.FeaturizeText("Features", "Text"))
                .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                    labelColumnName: "Label",
                    featureColumnName: "Features",
                    maximumNumberOfIterations: 100))
                .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);
            var model = pipeline.Fit(split.TrainSet);

            var predictions = model.Transform(split.TestSet);
            var metrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");

            _logger.LogInformation(
                "Training complete. MacroAccuracy: {Accuracy:P2}, MicroAccuracy: {Micro:P2}, LogLoss: {LogLoss:F4}",
                metrics.MacroAccuracy, metrics.MicroAccuracy, metrics.LogLoss);

            if (!Directory.Exists(modelsFolder))
                Directory.CreateDirectory(modelsFolder);

            var version = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var versionedPath = Path.Combine(modelsFolder, $"invoice_model_{version}.zip");
            var latestPath = Path.Combine(modelsFolder, "invoice_model_latest.zip");

            _mlContext.Model.Save(model, dataView.Schema, versionedPath);
            _logger.LogInformation("Versioned model saved: {Path}", versionedPath);

            File.Copy(versionedPath, latestPath, overwrite: true);
            _logger.LogInformation("Latest model updated: {Path}", latestPath);

            sw.Stop();
            _logger.LogInformation("Training pipeline finished in {Elapsed}ms", sw.ElapsedMilliseconds);

            return new LineTrainingResult
            {
                Success = true,
                Message = $"Model trained on {data.Count} samples. Accuracy: {metrics.MacroAccuracy:P2}.",
                Accuracy = metrics.MacroAccuracy,
                SampleCount = data.Count,
                ModelVersion = version,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Line classification training failed.");
            return new LineTrainingResult
            {
                Success = false,
                Message = $"Training failed: {ex.Message}",
            };
        }
    }
}
