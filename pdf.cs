using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia;
using Tesseract;
using System.Text.RegularExpressions;
using System.Linq;
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

    private static Dictionary<int, string> ExtractOcrTextFromPdfPages(
    string pdfPath,
    string tessdataPath = "C:\\Program Files\\Tesseract-OCR\\tessdata",
    string lang = "vie",
    float scale = 2.0f)
    {
        var result = new Dictionary<int, string>();

        using var engine = new TesseractEngine(tessdataPath, lang, EngineMode.Default);
        using var document = PdfDocument.Open(pdfPath, SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        for (int pageNum = 1; pageNum <= document.NumberOfPages; pageNum++)
        {
            try
            {
                using var pngStream = document.GetPageAsPng(pageNum, scale);
                var pngBytes = pngStream.ToArray();
                if (pngBytes.Length == 0)
                {
                    Console.WriteLine($"[OCR] Skipping page {pageNum}: empty render.");
                    continue;
                }

                using var pix = Pix.LoadFromMemory(pngBytes);
                if (pix == null)
                {
                    Console.WriteLine($"[OCR] Skipping page {pageNum}: cannot load image.");
                    continue;
                }

                using var page = engine.Process(pix);
                var text = page.GetText() ?? string.Empty;

                result[pageNum] = text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OCR] Skipping page {pageNum}: {ex.Message}");
            }
        }

        return result;
    }


    protected static async Task<string> ExtractPdfTextWithOcrFallbackAsync(string filePath)
    {
        const int minTextLengthForTrust = 300;

        using var doc = PdfDocument.Open(filePath);
        var pages = doc.GetPages().ToList();

        var pageTexts = new string[pages.Count];
        var needOcr = new bool[pages.Count];

        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var txt = ContentOrderTextExtractor.GetText(page) ?? string.Empty;

            var normalized = txt.Replace("\r", " ")
                                .Replace("\n", " ")
                                .Trim();

            if (!string.IsNullOrWhiteSpace(normalized) &&
                normalized.Length >= minTextLengthForTrust)
            {
                pageTexts[i] = normalized;
            }
            else
            {
                needOcr[i] = true;
            }
        }

        if (needOcr.All(b => b))
        {
            return ExtractOcrTextFromPdf(filePath);
        }

        if (needOcr.Any(b => b))
        {
            var ocrPages = ExtractOcrTextFromPdfPages(filePath);

            for (int i = 0; i < pageTexts.Length; i++)
            {
                if (!needOcr[i])
                    continue;

                var pageNum = i + 1;
                if (ocrPages.TryGetValue(pageNum, out var ocrText) &&
                    !string.IsNullOrWhiteSpace(ocrText))
                {
                    pageTexts[i] = ocrText.Trim();
                }
            }
        }

        var sb = new StringBuilder();
        for (int i = 0; i < pageTexts.Length; i++)
        {
            var pageText = pageTexts[i];
            if (string.IsNullOrWhiteSpace(pageText))
                continue;

            sb.AppendLine($"===== PAGE {i + 1} =====");
            sb.AppendLine(pageText);
            sb.AppendLine();
        }

        return sb.ToString();
    }
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
            try
            {
                using var pngStream = document.GetPageAsPng(pageNum, scale);
                var pngBytes = pngStream.ToArray();
                if (pngBytes.Length == 0)
                {
                    Console.WriteLine($"[OCR] Skipping page {pageNum}: empty render.");
                    continue;
                }

                using var pix = Pix.LoadFromMemory(pngBytes);
                if (pix == null)
                {
                    Console.WriteLine($"[OCR] Skipping page {pageNum}: cannot load image.");
                    continue;
                }

                using var page = engine.Process(pix);
                var text = page.GetText();

                sb.AppendLine($"===== PAGE {pageNum} =====");
                sb.AppendLine(text);
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OCR] Skipping page {pageNum}: {ex.Message}");
            }
        }
        
        return sb.ToString();
    }   

    private static List<string> ChunkText(string text, int maxChars = 1500)
    {
        var chunks = new List<string>();

        var cleaned = Regex.Replace(text, @"^===== PAGE \d+ =====\s*", string.Empty,
            RegexOptions.Multiline);

        var lines = cleaned.Split('\n');

        var sb = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                continue;
            }

            bool isHeading = Regex.IsMatch(
                line,
                @"^(Chương\s+\d+|Điều\s+\d+(\.|:)?\s)",
                RegexOptions.IgnoreCase
            );

            if (isHeading && sb.Length >= maxChars / 2)
            {
                chunks.Add(sb.ToString().Trim());
                sb.Clear();
            }

            if (sb.Length > 0 && sb.Length + line.Length + 1 > maxChars)
            {
                chunks.Add(sb.ToString().Trim());
                sb.Clear();
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
        {
            chunks.Add(sb.ToString().Trim());
        }

        return chunks;
    }

    public static async Task<bool> IngestPdfsAsync(string connectionString, string apiKey, HttpClient http)
    {
        var pdfFiles = ReadPdfFile();
        if (pdfFiles.Length == 0)
        {
            Console.WriteLine("No PDF files found in the 'pdfs' directory.");
            return false;
        }

        var docs = new List<Doc>();

        foreach (var pdfFile in pdfFiles)
        {
            Console.WriteLine($"Processing PDF: {pdfFile}");
            var text = await ExtractPdfTextWithOcrFallbackAsync(pdfFile);
            
            var chunks = ChunkText(text);
            
            int i = 0;
            foreach (var chunk in chunks)
            {
                docs.Add(new Doc($"pdf::{Path.GetFileName(pdfFile)}::{i}", chunk, $"pdf:{pdfFile}#chunk{i}"));
                i++;
            }
             
        }
        if (docs.Count == 0)
        {
            Console.WriteLine("No PDF text to ingest."); 
            return false; 
        }

        // Write all PDF chunks to a local file for inspection.
        var outputPath = "output.txt";
        var outputLines = docs.Select(d =>
            $"ID: {d.Id}\nMETA: {d.Tag}\nCONTENT:\n{d.Content}\n---");
        await File.WriteAllTextAsync(outputPath, string.Join("\n", outputLines));

        var sampleVec = await EmbedAsyncSingle(apiKey, "probe", http);
        await EnsureKbTableAsync(connectionString, sampleVec.Length, "kb_docs");

        const int batchSize = 100; 
        var allEmbeds = new List<float[]>(docs.Count);
        for (int i = 0; i < docs.Count; i += batchSize)
        {
            var batchDocs = docs.Skip(i).Take(batchSize).ToList();
            var batchEmbeds = await EmbedAsyncBatch(apiKey, batchDocs.Select(d => d.Content), http);
            allEmbeds.AddRange(batchEmbeds);
        }

        var kbDocs = docs.Select((d, idx) => new KbDoc(d.Id, d.Content, d.Tag, Normalize(allEmbeds[idx]))).ToList();
        await UpsertDocsAsync(connectionString, kbDocs, "kb_docs");
        Console.WriteLine($"Ingested {kbDocs.Count} PDF chunks.");

        return true;
    }
}
