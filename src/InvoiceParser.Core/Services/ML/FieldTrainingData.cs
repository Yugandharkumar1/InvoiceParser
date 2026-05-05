using Microsoft.ML.Data;

namespace InvoiceParser.Core.Services.ML;

public class FieldTrainingData
{
    [LoadColumn(0)]
    public string Context { get; set; } = string.Empty;

    [LoadColumn(1)]
    public string Label { get; set; } = string.Empty;
}

public class FieldPrediction
{
    [ColumnName("PredictedLabel")]
    public string PredictedLabel { get; set; } = string.Empty;

    [ColumnName("Score")]
    public float[] Score { get; set; } = Array.Empty<float>();
}

public class CandidateValue
{
    public string Value { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
}
