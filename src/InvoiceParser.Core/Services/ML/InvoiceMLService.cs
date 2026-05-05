using System.Globalization;
using System.Text.RegularExpressions;
using InvoiceParser.Core.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.ML;

namespace InvoiceParser.Core.Services.ML;

public class InvoiceMLService
{
    private readonly MLContext _mlContext;
    private readonly TrainingDataBuilder _dataBuilder;
    private readonly ILogger<InvoiceMLService> _logger;
    private readonly string _modelPath;
    private readonly object _lock = new();

    private ITransformer? _model;
    private PredictionEngine<FieldTrainingData, FieldPrediction>? _predictionEngine;

    public const int MinimumTrainingInvoices = 5;
    public int LastTrainedInvoiceCount { get; private set; }

    public bool IsModelReady
    {
        get
        {
            lock (_lock)
            {
                return _predictionEngine != null;
            }
        }
    }

    public InvoiceMLService(TrainingDataBuilder dataBuilder, ILogger<InvoiceMLService> logger,
        string modelPath = "ml-model/invoice-field-model.zip")
    {
        _mlContext = new MLContext(seed: 42);
        _dataBuilder = dataBuilder;
        _logger = logger;
        _modelPath = modelPath;

        TryLoadModel();
    }

    private void TryLoadModel()
    {
        try
        {
            if (File.Exists(_modelPath))
            {
                _model = _mlContext.Model.Load(_modelPath, out _);
                _predictionEngine = _mlContext.Model.CreatePredictionEngine<FieldTrainingData, FieldPrediction>(_model);
                _logger.LogInformation("ML model loaded from {Path}", _modelPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load existing ML model from {Path}", _modelPath);
        }
    }

    public TrainingResult Train(List<Invoice> invoices, List<InvoiceFeedback>? feedback = null)
    {
        var invoicesWithText = invoices.Where(i => !string.IsNullOrWhiteSpace(i.PdfText)).ToList();

        if (invoicesWithText.Count < MinimumTrainingInvoices)
        {
            _logger.LogInformation(
                "Not enough training data. Have {Count} invoices, need at least {Min}.",
                invoicesWithText.Count, MinimumTrainingInvoices);

            return new TrainingResult
            {
                Success = false,
                Message = $"Need at least {MinimumTrainingInvoices} saved invoices with PDF text. Currently have {invoicesWithText.Count}.",
            };
        }

        try
        {
            _logger.LogInformation("Building training data from {Count} invoices...", invoicesWithText.Count);
            var trainingData = _dataBuilder.BuildFromInvoices(invoicesWithText);

            if (feedback != null && feedback.Count > 0)
            {
                var feedbackSamples = _dataBuilder.BuildFromFeedback(feedback);
                _logger.LogInformation("Added {Count} training samples from user feedback.", feedbackSamples.Count);
                trainingData.AddRange(feedbackSamples);
            }

            if (trainingData.Count < 20)
            {
                return new TrainingResult
                {
                    Success = false,
                    Message = $"Not enough training samples generated ({trainingData.Count}). Save more invoices with complete data.",
                };
            }

            _logger.LogInformation("Training with {Count} samples...", trainingData.Count);
            var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

            var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label")
                .Append(_mlContext.Transforms.Text.FeaturizeText("ContextFeatures", "Context"))
                .Append(_mlContext.Transforms.Concatenate("Features", "ContextFeatures"))
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
                "Training complete. Accuracy: {Accuracy:P2}, Log-loss: {LogLoss:F4}",
                metrics.MacroAccuracy, metrics.LogLoss);

            var dir = Path.GetDirectoryName(_modelPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _mlContext.Model.Save(model, dataView.Schema, _modelPath);
            _logger.LogInformation("Model saved to {Path}", _modelPath);

            lock (_lock)
            {
                _model = model;
                _predictionEngine = _mlContext.Model.CreatePredictionEngine<FieldTrainingData, FieldPrediction>(_model);
            }

            LastTrainedInvoiceCount = invoicesWithText.Count;

            return new TrainingResult
            {
                Success = true,
                Message = $"Model trained successfully on {trainingData.Count} samples from {invoicesWithText.Count} invoices.",
                Accuracy = metrics.MacroAccuracy,
                SampleCount = trainingData.Count,
                InvoiceCount = invoicesWithText.Count,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model training failed.");
            return new TrainingResult
            {
                Success = false,
                Message = $"Training failed: {ex.Message}",
            };
        }
    }

    public ParsedInvoiceResult? Predict(string pdfText)
    {
        PredictionEngine<FieldTrainingData, FieldPrediction>? engine;
        lock (_lock)
        {
            engine = _predictionEngine;
        }

        if (engine == null) return null;

        try
        {
            var candidates = GenerateCandidates(pdfText);
            if (candidates.Count == 0) return null;

            var result = new ParsedInvoiceResult();
            var bestPredictions = new Dictionary<string, (string value, float confidence)>();

            foreach (var candidate in candidates)
            {
                var input = new FieldTrainingData
                {
                    Context = candidate.Context,
                    Label = string.Empty,
                };

                var prediction = engine.Predict(input);

                if (prediction.PredictedLabel == "none" || prediction.Score == null)
                    continue;

                var maxScore = prediction.Score.Length > 0 ? prediction.Score.Max() : 0f;
                if (maxScore < 0.3f) continue;

                if (!bestPredictions.ContainsKey(prediction.PredictedLabel) ||
                    maxScore > bestPredictions[prediction.PredictedLabel].confidence)
                {
                    bestPredictions[prediction.PredictedLabel] = (candidate.Value, maxScore);
                }
            }

            foreach (var (field, (value, confidence)) in bestPredictions)
            {
                _logger.LogDebug("ML predicted {Field} = {Value} (confidence: {Conf:P1})",
                    field, value, confidence);
                result.SummaryFields[field] = value;
            }

            _logger.LogInformation("ML model predicted {Count} summary fields.", result.SummaryFields.Count);
            return result.SummaryFields.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ML prediction failed.");
            return null;
        }
    }

    private static List<CandidateValue> GenerateCandidates(string pdfText)
    {
        var candidates = new List<CandidateValue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var patterns = new (Regex regex, string fieldType)[]
        {
            (new Regex(@"\b(\d{1,2}/\d{1,2}/\d{2,4})\b"), "date"),
            (new Regex(@"\b([A-Z][a-z]+ \d{1,2},? \d{4})\b"), "date"),
            (new Regex(@"\$?([\d,]+\.\d{2})\b"), "currency"),
            (new Regex(@"\b(\d{5,})\b"), "number"),
            (new Regex(@"\b(\d{3,}-[\d-]+)\b"), "number"),
        };

        foreach (var (regex, fieldType) in patterns)
        {
            foreach (Match match in regex.Matches(pdfText))
            {
                var value = match.Groups[1].Value.Trim();
                var key = $"{fieldType}:{value}";
                if (seen.Contains(key)) continue;
                seen.Add(key);

                var start = Math.Max(0, match.Index - 120);
                var end = Math.Min(pdfText.Length, match.Index + match.Length + 120);
                var context = Regex.Replace(pdfText[start..end], @"\s+", " ").Trim();

                candidates.Add(new CandidateValue
                {
                    Value = value,
                    Context = context,
                    FieldType = fieldType,
                });
            }
        }

        return candidates;
    }
}

public class TrainingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public double? Accuracy { get; set; }
    public int? SampleCount { get; set; }
    public int? InvoiceCount { get; set; }
}
