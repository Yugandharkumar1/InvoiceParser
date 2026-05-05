using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace InvoiceParser.Core.Services.ML;

public class InvoicePredictionService : IInvoicePredictionService
{
    private readonly MLContext _mlContext;
    private readonly ILogger<InvoicePredictionService> _logger;
    private readonly IFeedbackLookupService _feedbackLookup;
    private readonly string _modelPath;
    private readonly ReaderWriterLockSlim _rwLock = new();

    private ITransformer? _model;
    private PredictionEngine<LineClassificationData, LineClassificationPrediction>? _engine;
    private VBuffer<ReadOnlyMemory<char>> _labelBuffer;

    public InvoicePredictionService(ILogger<InvoicePredictionService> logger, string modelPath,
        IFeedbackLookupService feedbackLookup)
    {
        _mlContext = new MLContext(seed: 42);
        _logger = logger;
        _modelPath = modelPath;
        _feedbackLookup = feedbackLookup;

        TryLoadModel();
    }

    public bool IsModelReady
    {
        get
        {
            _rwLock.EnterReadLock();
            try { return _engine != null; }
            finally { _rwLock.ExitReadLock(); }
        }
    }

    public LinePredictionResult? Predict(string text)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_engine == null)
                return null;

            var input = new LineClassificationData { Text = text, Label = string.Empty };
            var prediction = _engine.Predict(input);

            if (prediction.Score == null || prediction.Score.Length == 0)
                return null;

            var scores = new Dictionary<string, float>();
            var labels = _labelBuffer.DenseValues().ToArray();
            for (int i = 0; i < labels.Length && i < prediction.Score.Length; i++)
            {
                scores[labels[i].ToString()] = prediction.Score[i];
            }

            var maxScore = prediction.Score.Max();

            return new LinePredictionResult
            {
                Label = prediction.PredictedLabel,
                Confidence = maxScore,
                Scores = scores,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prediction failed for text: {Text}", text.Length > 80 ? text[..80] + "..." : text);
            return null;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public LinePredictionResult? PredictWithFeedback(string text)
    {
        var feedbackResult = _feedbackLookup.Lookup(text);
        if (feedbackResult.IsMatch)
        {
            _logger.LogInformation("Feedback match used for: {Text} -> {Label} (confidence: {Conf:F2})",
                text.Length > 60 ? text[..60] + "..." : text,
                feedbackResult.CorrectedLabel, feedbackResult.Confidence);

            return new LinePredictionResult
            {
                Label = feedbackResult.CorrectedLabel,
                Confidence = (float)feedbackResult.Confidence,
                Scores = new() { [feedbackResult.CorrectedLabel] = (float)feedbackResult.Confidence },
            };
        }

        _logger.LogDebug("No feedback match, falling back to ML prediction for: {Text}",
            text.Length > 60 ? text[..60] + "..." : text);
        return Predict(text);
    }

    public void ReloadModel()
    {
        _rwLock.EnterWriteLock();
        try
        {
            LoadModelInternal();
            _logger.LogInformation("Prediction model hot-reloaded from {Path}", _modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload prediction model from {Path}", _modelPath);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    private void TryLoadModel()
    {
        try
        {
            if (File.Exists(_modelPath))
                LoadModelInternal();
            else
                _logger.LogInformation("No prediction model found at {Path}. Predictions disabled until training.", _modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load prediction model from {Path}", _modelPath);
        }
    }

    private void LoadModelInternal()
    {
        _model = _mlContext.Model.Load(_modelPath, out var schema);
        _engine = _mlContext.Model.CreatePredictionEngine<LineClassificationData, LineClassificationPrediction>(_model);

        var labelCol = schema.GetColumnOrNull("Label");
        if (labelCol.HasValue)
        {
            var slotNames = new VBuffer<ReadOnlyMemory<char>>();
            labelCol.Value.GetKeyValues(ref slotNames);
            _labelBuffer = slotNames;
        }

        _logger.LogInformation("Line classification model loaded from {Path}", _modelPath);
    }
}
