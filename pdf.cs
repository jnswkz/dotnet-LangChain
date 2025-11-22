using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia;
using Tesseract;
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
        else
        {
            return ExtractOcrTextFromPdf(filePath);
        }

    }

    // --- OCR extraction using Tesseract ---
    public static string ExtractOcrTextFromPdf(
        string pdfPath,
        string tessdataPath = "C:\\Program Files\\Tesseract-OCR\\tessdata",
        string lang = "vie",
        float scale = 2.0f)
    {
        using var engine = new TesseractEngine(tessdataPath, lang, EngineMode.Default);
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(pdfPath, SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        for (int pageNum = 1; pageNum <= document.NumberOfPages; pageNum++)
        {
            using var pngStream = document.GetPageAsPng(pageNum, scale);
            var pngBytes = pngStream.ToArray();

            using var pix = Pix.LoadFromMemory(pngBytes);
            using var page = engine.Process(pix);

            var text = page.GetText();

            sb.AppendLine($"===== PAGE {pageNum} =====");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString();
    }   

    private static List<string> ChunkTExt(string text, int chunkSize = 1000, int overlap = 200)
    {
        var chunks = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + chunkSize, text.Length);
            chunks.Add(text.Substring(start, end - start));
            start += chunkSize - overlap;
        }
        return chunks;
    }
    

    private static void IngestPdfsAsync(string connectionString, string apiKey, HttpClient http)
    {
        var pdfFiles = ReadPdfFile();
        if (pdfFiles.Length == 0)
        {
            Console.WriteLine("No PDF files found in the 'pdfs' directory.");
            return;
        }

        var docs = new List<Doc>();
        foreach (var pdfFile in pdfFiles)
        {
            Console.WriteLine($"Processing PDF: {pdfFile}");
            var text = ExtractPdfTextWithOcrFallbackAsync(pdfFile).Result;
            var chunks = ChunkTExt(text);
            
            int i = 0;
            foreach (var chunk in chunks)
            {
                docs.Add(new Doc($"pdf::{Path.GetFileName(pdfFile)}::{i}", chunk, $"pdf:{pdfFile}#chunk{i}"));
                i++;
            }
        }
        if (docs.Count == 0)
        {
            Console.WriteLine("No PDF text to ingest."); return false; 
        }

        var sampleVec = await EmbedAsyncSingle(apiKey, "probe", http);
        await EnsureKbTableAsync(connectionString, sampleVec.Length, "kb_docs");

        var embeds = await EmbedAsyncBatch(apiKey, docs.Select(d => d.Content), http);
        var kbDocs = docs.Select((d, idx) => new KbDoc(d.Id, d.Content, d.Metadata, Normalize(embeds[idx]))).ToList();
        await UpsertDocsAsync(connectionString, kbDocs, "kb_docs");
        Console.WriteLine($"Ingested {kbDocs.Count} PDF chunks.");
        return true;
    }
}
