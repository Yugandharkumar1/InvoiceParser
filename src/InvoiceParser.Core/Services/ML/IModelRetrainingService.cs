namespace InvoiceParser.Core.Services.ML;

public interface IModelRetrainingService
{
    Task<LineTrainingResult> RetrainAsync();
}
