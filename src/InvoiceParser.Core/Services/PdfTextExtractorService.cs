using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using InvoiceParser.Core.Ocr;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InvoiceParser.Core.Services;

public class PdfTextExtractorService
{
    private const int MinUsableTextLength = 50;

    private readonly ILogger<PdfTextExtractorService> _logger;
    private readonly IOcrService? _ocrService;

    public PdfTextExtractorService(ILogger<PdfTextExtractorService> logger, IOcrService? ocrService = null)
    {
        _logger = logger;
        _ocrService = ocrService;
    }

    public string ExtractText(Stream pdfStream)
    {
        using var ms = new MemoryStream();
        pdfStream.CopyTo(ms);
        var pdfBytes = ms.ToArray();

        // Tier 1: PdfPig (digital text with word-level positioning)
        try
        {
            var result = ExtractWithPdfPig(pdfBytes);
            if (!string.IsNullOrWhiteSpace(result) && result.Length >= MinUsableTextLength)
            {
                _logger.LogDebug("PdfPig extracted {Length} chars.", result.Length);
                return result;
            }

            _logger.LogWarning("PdfPig returned insufficient text ({Length} chars), trying PDFium.",
                result?.Length ?? 0);
        }
        catch (NotImplementedException ex)
        {
            _logger.LogWarning(ex, "PdfPig cannot parse this PDF (unsupported font type). Trying PDFium.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PdfPig failed to parse PDF. Trying PDFium.");
        }

        // Tier 2: PDFium/Docnet (different digital text parser)
        var docnetResult = ExtractWithDocnet(pdfBytes);
        if (!string.IsNullOrWhiteSpace(docnetResult) && docnetResult.Length >= MinUsableTextLength)
        {
            _logger.LogInformation("PDFium fallback extracted {Length} chars.", docnetResult.Length);
            return docnetResult;
        }

        _logger.LogWarning("PDFium returned insufficient text ({Length} chars). PDF may be scanned/image-based.",
            docnetResult?.Length ?? 0);

        // Tier 3: OCR (render PDF pages as images, then Tesseract)
        if (_ocrService?.IsAvailable == true)
        {
            try
            {
                var ocrResult = ExtractWithOcr(pdfBytes);
                if (!string.IsNullOrWhiteSpace(ocrResult))
                {
                    _logger.LogInformation("OCR fallback extracted {Length} chars from scanned PDF.", ocrResult.Length);
                    return ocrResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR fallback failed.");
            }
        }
        else
        {
            _logger.LogWarning("OCR service not available. Scanned/image PDFs cannot be processed.");
        }

        return docnetResult ?? string.Empty;
    }

    /// <summary>
    /// Extracts text from a raw image file (JPEG, PNG, TIFF, BMP) using OCR.
    /// </summary>
    public string ExtractTextFromImage(Stream imageStream)
    {
        if (_ocrService?.IsAvailable != true)
        {
            _logger.LogWarning("OCR service not available. Cannot extract text from image.");
            return string.Empty;
        }

        return _ocrService.ExtractTextFromImage(imageStream);
    }

    private static string ExtractWithPdfPig(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);
        var allLines = new List<string>();

        foreach (Page page in document.GetPages())
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0) continue;

            var lineGroups = new List<List<Word>>();
            var currentLine = new List<Word> { words[0] };
            var lastY = words[0].BoundingBox.Bottom;

            for (int i = 1; i < words.Count; i++)
            {
                var wordY = words[i].BoundingBox.Bottom;
                if (Math.Abs(wordY - lastY) > 5)
                {
                    lineGroups.Add(currentLine);
                    currentLine = new List<Word>();
                }
                currentLine.Add(words[i]);
                lastY = wordY;
            }
            if (currentLine.Count > 0)
                lineGroups.Add(currentLine);

            foreach (var group in lineGroups)
            {
                var ordered = group.OrderBy(w => w.BoundingBox.Left).ToList();
                var lineText = string.Join(" ", ordered.Select(w => w.Text));
                if (!string.IsNullOrWhiteSpace(lineText))
                    allLines.Add(lineText);
            }

            allLines.Add("");
        }

        return string.Join(Environment.NewLine, allLines);
    }

    private static string ExtractWithDocnet(byte[] pdfBytes)
    {
        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(1080, 1920));
        var pageCount = docReader.GetPageCount();
        var allLines = new List<string>();

        for (int i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            var text = pageReader.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lines = text.Split('\n', StringSplitOptions.None);
                foreach (var line in lines)
                {
                    var trimmed = line.TrimEnd('\r');
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        allLines.Add(trimmed);
                }
            }

            allLines.Add("");
        }

        return string.Join(Environment.NewLine, allLines);
    }

    private string ExtractWithOcr(byte[] pdfBytes)
    {
        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(2160, 3840));
        var pageCount = docReader.GetPageCount();
        var allText = new List<string>();

        for (int i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            var rawBytes = pageReader.GetImage();
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();

            if (rawBytes == null || rawBytes.Length == 0 || width <= 0 || height <= 0)
                continue;

            var pngBytes = ConvertBgraToPng(rawBytes, width, height);
            var pageText = _ocrService!.ExtractTextFromImage(pngBytes);

            if (!string.IsNullOrWhiteSpace(pageText))
                allText.Add(pageText.TrimEnd());

            _logger.LogDebug("OCR page {Page}/{Total}: {Length} chars", i + 1, pageCount, pageText?.Length ?? 0);
        }

        return string.Join(Environment.NewLine, allText);
    }

    /// <summary>
    /// Renders each PDF page and runs structured Tesseract OCR (words, lines, sections).
    /// Returns null when OCR is unavailable or no pages could be processed.
    /// </summary>
    public OcrStructuredDocument? ExtractStructuredOcrFromPdf(Stream pdfStream)
    {
        if (_ocrService?.IsAvailable != true)
        {
            _logger.LogWarning("OCR service not available. Cannot produce structured OCR for PDF.");
            return null;
        }

        using var ms = new MemoryStream();
        pdfStream.CopyTo(ms);
        var pdfBytes = ms.ToArray();

        try
        {
            using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(2160, 3840));
            var pageCount = docReader.GetPageCount();
            var pages = new List<OcrStructuredPage>();

            for (int i = 0; i < pageCount; i++)
            {
                using var pageReader = docReader.GetPageReader(i);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                if (rawBytes == null || rawBytes.Length == 0 || width <= 0 || height <= 0)
                    continue;

                var pngBytes = ConvertBgraToPng(rawBytes, width, height);
                var layout = _ocrService.ExtractStructuredPage(pngBytes);
                if (layout == null)
                    continue;

                layout.PageIndex = i;
                pages.Add(layout);
            }

            return pages.Count == 0 ? null : new OcrStructuredDocument { Pages = pages };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Structured OCR for PDF failed.");
            return null;
        }
    }

    /// <summary>Structured OCR for a single image stream (first page only).</summary>
    public OcrStructuredPage? ExtractStructuredOcrFromImage(Stream imageStream)
    {
        if (_ocrService?.IsAvailable != true)
            return null;
        return _ocrService.ExtractStructuredPage(imageStream);
    }

    private static byte[] ConvertBgraToPng(byte[] bgraBytes, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);

        var destPtr = bitmap.GetPixels();
        Marshal.Copy(bgraBytes, 0, destPtr, Math.Min(bgraBytes.Length, info.BytesSize));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    public static bool IsImageFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".tiff" or ".tif" or ".bmp" or ".webp";
    }
}
