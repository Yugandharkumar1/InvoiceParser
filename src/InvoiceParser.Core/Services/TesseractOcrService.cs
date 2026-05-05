using InvoiceParser.Core.Ocr;
using Microsoft.Extensions.Logging;
using Tesseract;

namespace InvoiceParser.Core.Services;

public class TesseractOcrService : IOcrService, IDisposable
{
    private readonly ILogger<TesseractOcrService> _logger;
    private readonly TesseractEngine? _engine;

    public bool IsAvailable { get; }

    public TesseractOcrService(ILogger<TesseractOcrService> logger, string tessDataPath)
    {
        _logger = logger;

        try
        {
            if (!Directory.Exists(tessDataPath))
            {
                _logger.LogWarning(
                    "Tesseract data folder not found at {Path}. OCR is disabled. " +
                    "Download eng.traineddata from https://github.com/tesseract-ocr/tessdata " +
                    "and place it in a 'tessdata' folder.", tessDataPath);
                IsAvailable = false;
                return;
            }

            var trainedDataFile = Path.Combine(tessDataPath, "eng.traineddata");
            if (!File.Exists(trainedDataFile))
            {
                _logger.LogWarning(
                    "eng.traineddata not found in {Path}. OCR is disabled. " +
                    "Download it from https://github.com/tesseract-ocr/tessdata", tessDataPath);
                IsAvailable = false;
                return;
            }

            _engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            IsAvailable = true;
            _logger.LogInformation("Tesseract OCR initialized (tessdata: {Path})", tessDataPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tesseract OCR could not be initialized. OCR is disabled.");
            IsAvailable = false;
        }
    }

    public string ExtractTextFromImage(byte[] imageBytes)
    {
        if (!IsAvailable || _engine == null)
            return string.Empty;

        try
        {
            using var pix = Pix.LoadFromMemory(imageBytes);
            using var page = _engine.Process(pix);

            var text = page.GetText();
            var confidence = page.GetMeanConfidence();

            _logger.LogInformation("OCR extracted {Length} chars (confidence: {Confidence:P0})",
                text.Length, confidence);

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR text extraction failed.");
            return string.Empty;
        }
    }

    public string ExtractTextFromImage(Stream imageStream)
    {
        using var ms = new MemoryStream();
        imageStream.CopyTo(ms);
        return ExtractTextFromImage(ms.ToArray());
    }

    public OcrStructuredPage? ExtractStructuredPage(byte[] imageBytes)
    {
        if (!IsAvailable || _engine == null)
            return null;

        try
        {
            using var pix = Pix.LoadFromMemory(imageBytes);
            var widthPx = pix.Width;
            var heightPx = pix.Height;
            using var page = _engine.Process(pix);
            using var iter = page.GetIterator();

            var raw = new List<WordLayoutRaw>();
            iter.Begin();
            do
            {
                var wordText = iter.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(wordText))
                    continue;
                if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var wb))
                    continue;

                var x1 = wb.X1;
                var y1 = wb.Y1;
                var x2 = wb.X2;
                var y2 = wb.Y2;
                var w = Math.Max(0, x2 - x1);
                var h = Math.Max(0, y2 - y1);

                float conf = 0f;
                if (iter is ResultIterator ri)
                    conf = ri.GetConfidence(PageIteratorLevel.Word);

                raw.Add(new WordLayoutRaw(
                    wordText.Trim(),
                    x1,
                    y1,
                    w,
                    h,
                    conf,
                    iter.IsAtBeginningOf(PageIteratorLevel.Block),
                    iter.IsAtBeginningOf(PageIteratorLevel.TextLine)));
            } while (iter.Next(PageIteratorLevel.Word));

            return BuildStructuredPage(0, widthPx, heightPx, raw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Structured OCR extraction failed.");
            return null;
        }
    }

    private static OcrStructuredPage BuildStructuredPage(int pageIndex, int widthPx, int heightPx, IReadOnlyList<WordLayoutRaw> raw)
    {
        var page = new OcrStructuredPage
        {
            PageIndex = pageIndex,
            WidthPx = widthPx,
            HeightPx = heightPx,
        };

        float sx = 1000f / Math.Max(widthPx, 1);
        float sy = 1000f / Math.Max(heightPx, 1);

        var keyed = new List<(int Block, int Line, WordLayoutRaw W)>();
        var b = -1;
        var l = -1;
        foreach (var w in raw)
        {
            if (w.NewBlock)
                b++;
            if (w.NewLine)
                l++;
            keyed.Add((Math.Max(0, b), Math.Max(0, l), w));
        }

        foreach (var (_, _, w) in keyed)
        {
            page.Words.Add(ToWordBox(w, sx, sy));
        }

        var lineGroups = keyed
            .GroupBy(t => (t.Block, t.Line))
            .OrderBy(g => g.Key.Item1)
            .ThenBy(g => g.Key.Item2)
            .Select(g => ToLineLayout(g.Key.Item1, g.Key.Item2, g.ToList(), sx, sy))
            .ToList();

        page.Lines.AddRange(lineGroups);

        var sectionIdx = 0;
        foreach (var blk in page.Lines.GroupBy(l => l.BlockIndex).OrderBy(g => g.Key))
        {
            var linesInBlock = blk.OrderBy(l => l.LineIndex).ToList();
            var sx0 = linesInBlock.Min(x => x.X);
            var sy0 = linesInBlock.Min(x => x.Y);
            var sx1 = linesInBlock.Max(x => x.X + x.Width);
            var sy1 = linesInBlock.Max(x => x.Y + x.Height);
            page.Sections.Add(new OcrSectionLayout
            {
                Index = sectionIdx++,
                Text = string.Join(Environment.NewLine, linesInBlock.Select(x => x.Text)),
                X = sx0,
                Y = sy0,
                Width = Math.Max(0, sx1 - sx0),
                Height = Math.Max(0, sy1 - sy0),
                Lines = linesInBlock,
            });
        }

        if (page.Sections.Count == 0 && page.Lines.Count > 0)
        {
            var ln = page.Lines;
            var bx0 = ln.Min(x => x.X);
            var by0 = ln.Min(x => x.Y);
            var bx1 = ln.Max(x => x.X + x.Width);
            var by1 = ln.Max(x => x.Y + x.Height);
            page.Sections.Add(new OcrSectionLayout
            {
                Index = 0,
                Text = string.Join(Environment.NewLine, ln.Select(x => x.Text)),
                X = bx0,
                Y = by0,
                Width = Math.Max(0, bx1 - bx0),
                Height = Math.Max(0, by1 - by0),
                Lines = new List<OcrLineLayout>(ln),
            });
        }

        return page;
    }

    private static OcrWordBox ToWordBox(WordLayoutRaw w, float sx, float sy) =>
        new()
        {
            Text = w.Text,
            X = w.X,
            Y = w.Y,
            Width = w.Width,
            Height = w.Height,
            Confidence = w.Confidence,
            BboxNorm = NormBox(w, sx, sy),
        };

    private static OcrLineLayout ToLineLayout(int block, int line,
        List<(int Block, int Line, WordLayoutRaw Word)> g, float sx, float sy)
    {
        var ordered = g.OrderBy(t => t.Word.X).ToList();
        var wordsInLine = ordered.Select(t => ToWordBox(t.Word, sx, sy)).ToList();
        var x0 = wordsInLine.Min(o => o.X);
        var y0 = wordsInLine.Min(o => o.Y);
        var x1 = wordsInLine.Max(o => o.X + o.Width);
        var y1 = wordsInLine.Max(o => o.Y + o.Height);
        return new OcrLineLayout
        {
            BlockIndex = block,
            LineIndex = line,
            Text = string.Join(" ", wordsInLine.Select(o => o.Text)),
            X = x0,
            Y = y0,
            Width = Math.Max(0, x1 - x0),
            Height = Math.Max(0, y1 - y0),
            Words = wordsInLine,
        };
    }

    private static float[] NormBox(WordLayoutRaw w, float sx, float sy) =>
        new[]
        {
            Clamp1000(w.X * sx),
            Clamp1000(w.Y * sy),
            Clamp1000((w.X + w.Width) * sx),
            Clamp1000((w.Y + w.Height) * sy),
        };

    private static float Clamp1000(float v) => Math.Clamp(v, 0f, 1000f);

    private readonly struct WordLayoutRaw
    {
        public readonly string Text;
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;
        public readonly float Confidence;
        public readonly bool NewBlock;
        public readonly bool NewLine;

        public WordLayoutRaw(string text, int x, int y, int width, int height, float confidence, bool newBlock, bool newLine)
        {
            Text = text;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Confidence = confidence;
            NewBlock = newBlock;
            NewLine = newLine;
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
        GC.SuppressFinalize(this);
    }
}
