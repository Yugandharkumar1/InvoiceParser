namespace InvoiceParser.Core.Services.ML;

public interface IInvoiceMLTrainer
{
    LineTrainingResult Train(List<LineClassificationData> data, string modelsFolder);
}
