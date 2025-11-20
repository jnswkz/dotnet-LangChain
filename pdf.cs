using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

partial class Program
{
    protected static string[] ReadPdfFile()
    {
        string dir = "./pdfs/";
        // string[] file_names;
        if (Directory.Exists(dir))
        {
            return Directory.GetFiles(dir, "*.pdf");
        }
        else
        {
            throw new DirectoryNotFoundException($"The directory {dir} does not exist.");
        }
    }    

    protected static async Task<string> ExtractPdfTextWithOcrFallbackAsync(string filePath)
    {
        using var doc = PdfDocument.Open(filePath);
        var sb = new StringBuilder();
        var totalLetters = 0;

        // Pass 1: native text
        foreach (var page in doc.GetPages())
        {
            totalLetters += page.Letters.Count;
            var txt = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(txt))
                sb.AppendLine(txt);
        }
        if (totalLetters > 0 && sb.Length > 0)
        {
            return sb.ToString();
        }

        // Pass 2: OCR per page
        var ocrSb = new StringBuilder();
        int pageIndex = 0;
        foreach (var page in doc.GetPages())
        {
            pageIndex++;
            // render page to image bytes/stream (depends on your renderer)
            // var image = RenderPageToPng(page);
            // var ocrText = await RunOcrAsync(image);
            var ocrText = await RunOcrAsyncFallback(page); // placeholder
            if (!string.IsNullOrWhiteSpace(ocrText))
                ocrSb.AppendLine(ocrText);
        }

        return ocrSb.Length > 0 ? ocrSb.ToString() : string.Empty;
    }

    // Placeholder: wire this to your OCR of choice (Tesseract CLI or cloud service).
    private static Task<string> RunOcrAsyncFallback(object renderedPage)
    {
        throw new NotImplementedException("Connect to OCR (e.g., Tesseract or cloud API) here.");
    }
}
