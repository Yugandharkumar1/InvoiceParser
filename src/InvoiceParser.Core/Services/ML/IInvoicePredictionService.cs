namespace InvoiceParser.Core.Services.ML;

public interface IInvoicePredictionService
{
    bool IsModelReady { get; }
    LinePredictionResult? Predict(string text);
    LinePredictionResult? PredictWithFeedback(string text);
    void ReloadModel();
}
