namespace InvoiceParser.Core.Services.ML;

public interface IFeedbackTrainingDataService
{
    Task<int> ExportFeedbackToCsvAsync(string outputFolder);
}
