using InvoiceParser.Core.Ocr;

namespace InvoiceParser.Core.Services;

public interface IOcrService
{
    bool IsAvailable { get; }
    string ExtractTextFromImage(byte[] imageBytes);
    string ExtractTextFromImage(Stream imageStream);

    /// <summary>Word boxes, lines, and sections (Tesseract layout). Null when OCR unavailable or on error.</summary>
    OcrStructuredPage? ExtractStructuredPage(byte[] imageBytes) => null;

    OcrStructuredPage? ExtractStructuredPage(Stream imageStream)
    {
        using var ms = new MemoryStream();
        imageStream.CopyTo(ms);
        return ExtractStructuredPage(ms.ToArray());
    }
}
