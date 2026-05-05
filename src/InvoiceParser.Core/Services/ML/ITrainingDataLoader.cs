namespace InvoiceParser.Core.Services.ML;

public interface ITrainingDataLoader
{
    List<LineClassificationData> LoadAll();
}
