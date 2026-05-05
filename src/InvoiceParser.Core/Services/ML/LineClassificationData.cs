using Microsoft.ML.Data;

namespace InvoiceParser.Core.Services.ML;

public class LineClassificationData
{
    [LoadColumn(0)]
    public string Text { get; set; } = string.Empty;

    [LoadColumn(1)]
    public string Label { get; set; } = string.Empty;
}

public class LineClassificationPrediction
{
    [ColumnName("PredictedLabel")]
    public string PredictedLabel { get; set; } = string.Empty;

    [ColumnName("Score")]
    public float[] Score { get; set; } = Array.Empty<float>();
}

public class LineTrainingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public double? Accuracy { get; set; }
    public int? SampleCount { get; set; }
    public string? ModelVersion { get; set; }
}

public class LinePredictionResult
{
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public Dictionary<string, float> Scores { get; set; } = new();
}

public class FeedbackMatchResult
{
    public bool IsMatch { get; set; }
    public string CorrectedLabel { get; set; } = string.Empty;
    public double Confidence { get; set; }
}
