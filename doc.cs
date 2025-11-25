using Xceed.Words.NET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Enhanced record with more metadata for better retrieval
record EnhancedDocxChunk(
    string DocumentId,
    string DocumentTitle,
    int ChunkIndex,
    string SectionTitle,
    string SectionHierarchy,  // e.g., "Điều 5 > Khoản 2"
    string Content,
    string ContentType,       // "regulation", "procedure", "definition", "table", "general"
    List<string> Keywords     // extracted key terms
);

partial class Program
{
    // Vietnamese heading patterns for academic regulations - IMPROVED
    private static readonly Regex HeadingPatterns = new Regex(
        @"(Điều\s+\d+[\.\:]|Chương\s+[IVXLCDM\d]+[\.\:]?|Mục\s+\d+[\.\:]?|Khoản\s+\d+[\.\:]?|Phần\s+[IVXLCDM\d]+[\.\:]?|" +
        @"CHƯƠNG\s+[IVXLCDM\d]+|MỤC\s+\d+|PHẦN\s+[IVXLCDM\d]+|" +
        @"\d+\.\s+[A-ZĐÀÁẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬÈÉẺẼẸÊẾỀỂỄỆÌÍỈĨỊÒÓỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢÙÚỦŨỤƯỨỪỬỮỰỲÝỶỸỴ]|" +
        @"[a-z]\)\s+[A-ZĐÀÁẢÃẠ])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Pattern to detect article/section numbers - IMPROVED
    private static readonly Regex ArticlePattern = new Regex(
        @"Điều\s+(\d+)[\.\:\s]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex ChapterPattern = new Regex(
        @"Chương\s+([IVXLCDM\d]+)[\.\:\s]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    protected static string[] ReadDocFiles()
    {
        string dir = "./docx/";
        if (Directory.Exists(dir))
        {
            return Directory.GetFiles(dir, "*.docx");
        }
        else
        {
            throw new DirectoryNotFoundException($"The directory {dir} does not exist.");
        }
    }

    /// <summary>
    /// Extract structured content from DOCX with heading detection
    /// Tables are now processed inline to preserve their context with preceding headings
    /// </summary>
    protected static List<(string Text, bool IsHeading, int Level)> GetStructuredTextFromDocx(string filePath)
    {
        using var document = DocX.Load(filePath);
        var elements = new List<(string Text, bool IsHeading, int Level)>();
        
        // Track table references to detect "Bảng X" labels
        string? pendingTableLabel = null;

        // Process paragraphs with heading detection
        foreach (var p in document.Paragraphs)
        {
            var text = p.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Check if this is a table label (e.g., "Bảng 4. ...")
            if (Regex.IsMatch(text, @"^Bảng\s+\d+", RegexOptions.IgnoreCase))
            {
                pendingTableLabel = text;
            }

            // Detect heading level based on style or pattern
            int headingLevel = DetectHeadingLevel(text);
            bool isHeading = headingLevel > 0;

            elements.Add((text, isHeading, headingLevel));
        }

        // Process tables - try to match with their labels
        int tableIndex = 0;
        foreach (var table in document.Tables)
        {
            tableIndex++;
            var tableContent = ExtractTableAsStructuredText(table);
            if (!string.IsNullOrWhiteSpace(tableContent))
            {
                // Create a searchable table representation
                var tableText = $"[BẢNG {tableIndex}]\n{tableContent}";
                elements.Add((tableText, false, 0));
            }
        }

        return elements;
    }

    /// <summary>
    /// Alternative: Get raw text with tables embedded at approximate positions
    /// </summary>
    protected static string GetRawTextFromDocxWithInlineTables(string filePath)
    {
        using var document = DocX.Load(filePath);
        var sb = new StringBuilder();
        
        // Create a dictionary of table contents indexed by their approximate position
        var tableContents = new Dictionary<int, string>();
        int tableIdx = 0;
        foreach (var table in document.Tables)
        {
            var content = ExtractTableAsStructuredText(table);
            if (!string.IsNullOrWhiteSpace(content))
            {
                tableContents[tableIdx++] = content;
            }
        }

        string? currentTableLabel = null;
        int usedTableIdx = 0;

        foreach (var p in document.Paragraphs)
        {
            var text = p.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Detect table labels
            var tableMatch = Regex.Match(text, @"^(Bảng\s+\d+[^:]*):?\s*(.*)", RegexOptions.IgnoreCase);
            if (tableMatch.Success)
            {
                currentTableLabel = tableMatch.Groups[1].Value;
                sb.AppendLine();
                sb.AppendLine($"### {text}");
                
                // Insert the next table content right after its label
                if (usedTableIdx < tableContents.Count)
                {
                    sb.AppendLine(tableContents[usedTableIdx]);
                    usedTableIdx++;
                }
                sb.AppendLine();
                continue;
            }

            int headingLevel = DetectHeadingLevel(text);
            if (headingLevel > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"### {text}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        // Append any remaining tables that weren't matched to labels
        while (usedTableIdx < tableContents.Count)
        {
            sb.AppendLine();
            sb.AppendLine($"### [BẢNG {usedTableIdx + 1}]");
            sb.AppendLine(tableContents[usedTableIdx]);
            usedTableIdx++;
        }

        return sb.ToString().Trim();
    }

    private static int DetectHeadingLevel(string text)
    {
        // Normalize: trim and remove common leading characters
        var normalized = text.Trim();
        
        // Remove common leading patterns like "1.", "2.", "a)", "b)", bullets, etc.
        normalized = Regex.Replace(normalized, @"^[\s\-\•\·\*\◦\▪\►]+", "");
        normalized = normalized.TrimStart();
        
        // Check for Chapter (highest level) - more flexible matching
        // Matches: "CHƯƠNG 1", "Chương I", "CHƯƠNG 2:", "Chương II.", etc.
        if (Regex.IsMatch(normalized, @"^(CHƯƠNG|Chương)\s+[IVXLCDM\d]+[\.\:\s]?", RegexOptions.IgnoreCase))
            return 1;
        
        // Check for Part - "PHẦN I", "Phần 1", etc.
        if (Regex.IsMatch(normalized, @"^(PHẦN|Phần)\s+[IVXLCDM\d]+[\.\:\s]?", RegexOptions.IgnoreCase))
            return 1;

        // Check for Section (Mục) - "MỤC 1", "Mục 2.", etc.
        if (Regex.IsMatch(normalized, @"^(MỤC|Mục)\s+\d+[\.\:\s]?", RegexOptions.IgnoreCase))
            return 2;

        // Check for Article (Điều) - IMPROVED: more flexible patterns
        // Matches: "Điều 16", "Điều 16.", "Điều 16:", "Điều 16. Xử lý học vụ", etc.
        // Also handles: "  Điều 16" (with leading spaces - already trimmed above)
        if (Regex.IsMatch(normalized, @"^Điều\s+\d+[\.\:\s]?", RegexOptions.IgnoreCase))
            return 3;
        
        // Also check for "Điều" appearing anywhere in short text (likely a heading)
        if (text.Length < 150 && Regex.IsMatch(text, @"Điều\s+\d+[\.\:]", RegexOptions.IgnoreCase))
            return 3;

        // Check for Clause (Khoản) - "1. Cảnh báo học vụ:", "2. Đình chỉ:", etc.
        // More comprehensive Vietnamese uppercase letters
        if (Regex.IsMatch(normalized, @"^\d+\.\s+[A-ZĐÀÁẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬÈÉẺẼẸÊẾỀỂỄỆÌÍỈĨỊÒÓỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢÙÚỦŨỤƯỨỪỬỮỰỲÝỶỸỴ]"))
            return 4;
        
        // Check for lettered clauses - "a) Điểm...", "b) Sinh viên...", etc.
        if (Regex.IsMatch(normalized, @"^[a-zđ]\)\s+[A-ZĐÀÁẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬÈÉẺẼẸÊẾỀỂỄỆÌÍỈĨỊÒÓỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢÙÚỦŨỤƯỨỪỬỮỰỲÝỶỸỴ]"))
            return 5;

        // Check if text is short and looks like a title (all caps)
        if (text.Length < 100 && text.Length > 5 && text == text.ToUpper() && !text.Contains(". "))
            return 2;
        
        // Check for common heading keywords at the start
        var headingKeywords = new[] { "QUY ĐỊNH", "QUY CHẾ", "HƯỚNG DẪN", "THỦ TỤC", "ĐIỀU KIỆN", "YÊU CẦU" };
        foreach (var keyword in headingKeywords)
        {
            if (normalized.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) && text.Length < 120)
                return 2;
        }

        return 0;
    }

    private static string ExtractTableAsStructuredText(dynamic table)
    {
        var sb = new StringBuilder();
        bool isFirstRow = true;
        var headers = new List<string>();

        try
        {
            var allRows = new List<List<string>>();
            
            foreach (var row in table.Rows)
            {
                var cells = new List<string>();
                foreach (var cell in row.Cells)
                {
                    var cellText = string.Join(" ", 
                        ((IEnumerable<dynamic>)cell.Paragraphs)
                            .Select(p => ((string?)p.Text)?.Trim())
                            .Where(t => !string.IsNullOrWhiteSpace(t)));
                    cells.Add(cellText);
                }
                allRows.Add(cells);
            }

            if (allRows.Count == 0) return "";

            // Detect if first row is header (shorter text, likely column names)
            var firstRow = allRows[0];
            bool hasHeader = firstRow.All(c => c.Length < 80) && allRows.Count > 1;

            if (hasHeader)
            {
                headers = firstRow;
                allRows = allRows.Skip(1).ToList();
            }

            // Format each row with headers for better searchability
            foreach (var row in allRows)
            {
                if (headers.Count > 0 && headers.Count == row.Count)
                {
                    // Format as "Header1: Value1; Header2: Value2; ..."
                    // This makes it much easier to search for specific values
                    var pairs = new List<string>();
                    for (int i = 0; i < headers.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(row[i]))
                        {
                            pairs.Add($"{headers[i]}: {row[i]}");
                        }
                    }
                    if (pairs.Count > 0)
                    {
                        sb.AppendLine(string.Join("; ", pairs));
                    }
                }
                else
                {
                    // No headers or mismatch - just join cells
                    var nonEmpty = row.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                    if (nonEmpty.Count > 0)
                    {
                        sb.AppendLine(string.Join(" | ", nonEmpty));
                    }
                }
            }
        }
        catch
        {
            // If dynamic parsing fails, return empty
        }

        return sb.ToString().Trim();
    }

    protected static string GetRawTextFromDocx(string filePath)
    {
        // Use the new method that embeds tables inline with their labels
        return GetRawTextFromDocxWithInlineTables(filePath);
    }


    /// <summary>
    /// Enhanced semantic chunking that preserves document structure and context
    /// </summary>
    protected static List<EnhancedDocxChunk> ChunkTextSemantic(
        string documentId,
        string documentTitle,
        string fullText,
        int maxChunkSize = 1500,     // Smaller chunks for better precision
        int minChunkSize = 300,      // Avoid tiny chunks
        int contextOverlap = 150)    // Overlap for context continuity
    {
        var chunks = new List<EnhancedDocxChunk>();
        if (string.IsNullOrWhiteSpace(fullText)) return chunks;

        fullText = fullText.Replace("\r\n", "\n");
        var lines = fullText.Split('\n');

        var currentChunk = new StringBuilder();
        var currentSection = "Tổng quan";
        var sectionHierarchy = new List<string>();
        int chunkIndex = 0;
        string? lastHeading = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Detect section/heading changes
            if (trimmed.StartsWith("### "))
            {
                // Flush current chunk before new section
                if (currentChunk.Length >= minChunkSize)
                {
                    chunks.Add(CreateEnhancedChunk(
                        documentId, documentTitle, chunkIndex++,
                        currentSection, string.Join(" > ", sectionHierarchy),
                        currentChunk.ToString().Trim(), lastHeading));
                    currentChunk.Clear();
                }

                var heading = trimmed.Substring(4).Trim();
                UpdateSectionHierarchy(sectionHierarchy, heading);
                currentSection = heading;
                lastHeading = heading;
                
                // Include heading in chunk for context
                currentChunk.AppendLine(heading);
                continue;
            }

            // Check if adding this line exceeds max size
            if (currentChunk.Length + trimmed.Length > maxChunkSize && currentChunk.Length >= minChunkSize)
            {
                // Flush chunk
                var content = currentChunk.ToString().Trim();
                chunks.Add(CreateEnhancedChunk(
                    documentId, documentTitle, chunkIndex++,
                    currentSection, string.Join(" > ", sectionHierarchy),
                    content, lastHeading));

                // Start new chunk with overlap context
                currentChunk.Clear();
                if (!string.IsNullOrEmpty(currentSection))
                {
                    currentChunk.AppendLine($"[Tiếp theo: {currentSection}]");
                }
                
                // Add last few sentences as context overlap
                var overlapContent = GetLastSentences(content, contextOverlap);
                if (!string.IsNullOrEmpty(overlapContent))
                {
                    currentChunk.AppendLine(overlapContent);
                }
            }

            currentChunk.AppendLine(trimmed);
        }

        // Flush remaining content
        if (currentChunk.Length > 0)
        {
            chunks.Add(CreateEnhancedChunk(
                documentId, documentTitle, chunkIndex++,
                currentSection, string.Join(" > ", sectionHierarchy),
                currentChunk.ToString().Trim(), lastHeading));
        }

        return chunks;
    }

    private static void UpdateSectionHierarchy(List<string> hierarchy, string newHeading)
    {
        // Detect heading level and update hierarchy
        if (Regex.IsMatch(newHeading, @"^(CHƯƠNG|Chương)", RegexOptions.IgnoreCase))
        {
            hierarchy.Clear();
            hierarchy.Add(newHeading);
        }
        else if (Regex.IsMatch(newHeading, @"^Điều\s+\d+", RegexOptions.IgnoreCase))
        {
            while (hierarchy.Count > 1) hierarchy.RemoveAt(hierarchy.Count - 1);
            hierarchy.Add(newHeading);
        }
        else if (Regex.IsMatch(newHeading, @"^\d+\.", RegexOptions.IgnoreCase))
        {
            while (hierarchy.Count > 2) hierarchy.RemoveAt(hierarchy.Count - 1);
            hierarchy.Add(newHeading);
        }
        else
        {
            if (hierarchy.Count > 3) hierarchy.RemoveAt(hierarchy.Count - 1);
            hierarchy.Add(newHeading);
        }
    }

    private static string GetLastSentences(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        
        // Find sentence boundaries
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
        var result = new StringBuilder();
        
        for (int i = sentences.Length - 1; i >= 0 && result.Length < maxLength; i--)
        {
            result.Insert(0, sentences[i] + " ");
        }
        
        return result.ToString().Trim();
    }

    private static EnhancedDocxChunk CreateEnhancedChunk(
        string documentId, string documentTitle, int index,
        string section, string hierarchy, string content, string? lastHeading)
    {
        var contentType = DetectContentType(content);
        var keywords = ExtractKeywords(content);

        return new EnhancedDocxChunk(
            DocumentId: documentId,
            DocumentTitle: documentTitle,
            ChunkIndex: index,
            SectionTitle: section,
            SectionHierarchy: hierarchy,
            Content: content,
            ContentType: contentType,
            Keywords: keywords
        );
    }

    private static string DetectContentType(string content)
    {
        var lower = content.ToLower();
        
        if (lower.Contains("điều kiện") || lower.Contains("yêu cầu") || lower.Contains("phải"))
            return "requirement";
        if (lower.Contains("quy trình") || lower.Contains("bước") || lower.Contains("thực hiện"))
            return "procedure";
        if (lower.Contains("định nghĩa") || lower.Contains("là gì") || lower.Contains("nghĩa là"))
            return "definition";
        if (lower.Contains("[bảng]"))
            return "table";
        if (Regex.IsMatch(lower, @"điều\s+\d+"))
            return "regulation";
        
        return "general";
    }

    private static List<string> ExtractKeywords(string content)
    {
        var keywords = new List<string>();
        
        // Extract key Vietnamese academic terms
        var patterns = new[]
        {
            @"tốt nghiệp",
            @"xét tốt nghiệp",
            @"công nhận tốt nghiệp",
            @"điểm trung bình",
            @"tín chỉ",
            @"học phí",
            @"đăng ký học phần",
            @"điều kiện",
            @"khóa luận",
            @"đồ án",
            @"bảo vệ",
            @"học vụ",
            @"cảnh báo học vụ",
            @"xử lý học vụ",
            @"đình chỉ",
            @"đình chỉ học tập",
            @"buộc thôi học",
            @"thôi học",
            @"bảo lưu",
            @"chuyển ngành",
            @"miễn học",
            @"công nhận tín chỉ",
            @"ĐTBHK",
            @"vi phạm",
            @"kỷ luật",
            @"thi hộ",
            @"gian lận"
        };

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
            {
                keywords.Add(pattern);
            }
        }

        // Extract article references
        var articleMatches = Regex.Matches(content, @"Điều\s+\d+", RegexOptions.IgnoreCase);
        foreach (Match m in articleMatches)
        {
            keywords.Add(m.Value.ToLower());
        }

        return keywords.Distinct().ToList();
    }

    // Keep old method for backward compatibility
    protected static List<DocxChunk> ChunkText(
        string documentId,
        string fullText,
        int maxChunkSize = 2000,
        int overlap = 200)
    {
        // Use new semantic chunking and convert to old format
        var enhanced = ChunkTextSemantic(documentId, documentId, fullText, maxChunkSize, 300, overlap);
        return enhanced.Select(e => new DocxChunk(
            DocumentId: e.DocumentId,
            ChunkIndex: e.ChunkIndex,
            SectionTitle: e.SectionTitle,
            Content: e.Content
        )).ToList();
    }

    public static async Task<bool> IngestDocXAsync(string connectionString, string apiKey, HttpClient http)
    {
        var docxFiles = ReadDocFiles();
        if (docxFiles.Length == 0)
        {
            Console.WriteLine("No .docx files found to ingest.");
            return false;
        }

        var allChunks = new List<EnhancedDocxChunk>();

        foreach (var filePath in docxFiles)
        {
            var documentId = Path.GetFileName(filePath);
            
            try
            {
                var text = GetRawTextFromDocx(filePath);
                var documentTitle = ExtractDocumentTitle(filePath);
                
                Console.WriteLine($"Processing: {documentId}");
                
                var chunks = ChunkTextSemantic(documentId, documentTitle, text, 
                    maxChunkSize: 1200,    // Smaller for precision
                    minChunkSize: 200,
                    contextOverlap: 100);
                
                Console.WriteLine($"  -> {chunks.Count} chunks created");
                allChunks.AddRange(chunks);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"⚠️ Skipping {documentId}: File is locked or in use ({ex.Message})");
                continue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error processing {documentId}: {ex.Message}");
                continue;
            }
        }

        // Create enriched text for embedding (includes metadata for better semantic matching)
        var textsForEmbedding = allChunks.Select(c => CreateEnrichedTextForEmbedding(c)).ToList();
        var embeddings = await EmbedAsyncBatch(apiKey, textsForEmbedding, http);

        var kbDocs = new List<KbDoc>();
        for (int i = 0; i < allChunks.Count; i++)
        {
            var chunk = allChunks[i];
            var embedding = Normalize(embeddings[i]);
            
            // Rich metadata for filtering and display
            var metadata = string.Join(";", new[]
            {
                $"doc:{chunk.DocumentId}",
                $"title:{chunk.DocumentTitle}",
                $"section:{chunk.SectionTitle}",
                $"hierarchy:{chunk.SectionHierarchy}",
                $"type:{chunk.ContentType}",
                $"keywords:{string.Join(",", chunk.Keywords)}"
            });

            kbDocs.Add(new KbDoc(
                Id: $"docx::{chunk.DocumentId}::{chunk.ChunkIndex}",
                Content: chunk.Content,
                Metadata: metadata,
                Embedding: embedding
            ));
        }

        await UpsertDocsAsync(connectionString, kbDocs);

        Console.WriteLine($"\n✅ Ingested {kbDocs.Count} enhanced docx chunks into the vector store.");
        return true;
    }

    private static string ExtractDocumentTitle(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        // Clean up common prefixes/suffixes
        fileName = Regex.Replace(fileName, @"^\d+[-_]", "");
        fileName = Regex.Replace(fileName, @"[-_]\d+$", "");
        fileName = fileName.Replace("_", " ").Replace("-", " ");
        return fileName.Trim();
    }

    private static string CreateEnrichedTextForEmbedding(EnhancedDocxChunk chunk)
    {
        var sb = new StringBuilder();
        
        // Add document context for better embedding
        sb.AppendLine($"Tài liệu: {chunk.DocumentTitle}");
        
        if (!string.IsNullOrEmpty(chunk.SectionHierarchy))
        {
            sb.AppendLine($"Phần: {chunk.SectionHierarchy}");
        }
        else if (!string.IsNullOrEmpty(chunk.SectionTitle) && chunk.SectionTitle != "Tổng quan")
        {
            sb.AppendLine($"Mục: {chunk.SectionTitle}");
        }

        // Add keywords as semantic hints
        if (chunk.Keywords.Count > 0)
        {
            sb.AppendLine($"Từ khóa: {string.Join(", ", chunk.Keywords)}");
        }

        sb.AppendLine();
        sb.Append(chunk.Content);

        return sb.ToString();
    }
}
