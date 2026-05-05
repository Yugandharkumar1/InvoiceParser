using InvoiceParser.Core.Services;
using Microsoft.Extensions.Logging;

var pdfPath = args.Length > 0
    ? args[0]
    : @"e:\Projects\Tested Files\Verizon Wireless\Verizon Wireless - 542404686-00001 - 022326 - 292904.pdf";

if (!File.Exists(pdfPath))
{
    Console.WriteLine($"File not found: {pdfPath}");
    return;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var extractor = new PdfTextExtractorService(loggerFactory.CreateLogger<PdfTextExtractorService>());
using var stream = File.OpenRead(pdfPath);
var text = extractor.ExtractText(stream);

var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
for (int i = 0; i < lines.Length; i++)
{
    Console.WriteLine($"{i + 1,5}| {lines[i]}");
}
