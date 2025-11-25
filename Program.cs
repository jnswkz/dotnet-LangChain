using dotenv.net;
using LangChain.Providers;
using LangChain.Providers.Google;
using System.IO;
using Npgsql;

DotEnv.Load();

// Console.InputEncoding  = Encoding.UTF8;
// Console.OutputEncoding = Encoding.UTF8;

// var pdfFiles = ReadPdfFile();
// // for (var i = 0; i < pdfFiles.Length; i++)
// // {
// //     Console.WriteLine($"PDF File {i + 1}/{pdfFiles.Length}: {pdfFiles[i]}");
// // }
// // open output.txt and write
// await File.WriteAllTextAsync("output.txt", await ExtractPdfTextWithOcrFallbackAsync(pdfFiles[0]));
// return;
var env = DotEnv.Read();

if (!env.TryGetValue("GOOGLE_API_KEY", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("GOOGLE_API_KEY is missing from your environment (.env or secrets).");
    return;
}

if (!env.TryGetValue("AZURE_POSTGRES_URL", out var connectionString) || string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("AZURE_POSTGRES_URL is missing from your environment (.env or secrets).");
    return;
}

DescribePostgresTarget(connectionString);

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(120)  // Increased timeout for longer responses
};

var googleConfig = new GoogleConfiguration
{
    ApiKey = apiKey,
    Temperature = 0.3,
    TopP = 0.95,
    MaxOutputTokens = 4096  // Increased for complete answers
};

var googleProvider = new GoogleProvider(googleConfig, httpClient);
var geminiModel = new GoogleChatModel(googleProvider, "gemini-2.5-pro");

var forceReingest = env.TryGetValue("FORCE_REINGEST", out var forceFlag) &&
                    string.Equals(forceFlag, "1", StringComparison.OrdinalIgnoreCase);
var hasKbDocs = await HasKbDocsAsync(connectionString, "kb_docs");

if (forceReingest || !hasKbDocs)
{
    await ClearVectorStoreAsync(connectionString);
    var ingested = await SyncVectorStoreAsync(connectionString, apiKey, httpClient);
    if (!ingested)
    {
        return;
    }

    var docXIngested = await IngestDocXAsync(connectionString, apiKey, httpClient);
    if (!docXIngested)
    {
        Console.WriteLine("DOCX ingestion skipped or failed.");
    }
}
else
{
    Console.WriteLine("kb_docs already populated; skipping ingestion. Set FORCE_REINGEST=1 to refresh.");
}



// --- 4) RAG Q&A ---
var question = "Điều kiện xét tốt nghiệp và công nhận tốt nghiệp";

Console.Write("\nEnter your question: ");
Console.WriteLine($"\nYou> {question}");

// Use RETRIEVAL_QUERY task type for questions (better semantic matching)
var qVec = Normalize(await EmbedAsyncSingle(apiKey, question, httpClient, isQuery: true));
var hits = await HybridSearchAsync(connectionString, question, qVec, k: 10, table: "kb_docs");



// handle no hits
if (hits.Count == 0 )
{

    var systemPrompt = @"
Bạn là trợ lý ảo tích hợp trong ứng dụng học vụ của Trường Đại học Công nghệ Thông Tin.

Vai trò của bạn:
- Hỗ trợ sinh viên, giảng viên và cán bộ hiểu và sử dụng ứng dụng học vụ.
- Giải thích các quy chế, quy định, quy trình liên quan đến đào tạo, học vụ, điểm số, kết quả học tập... dựa trên các tài liệu mà hệ thống đã index.
- Hướng dẫn người dùng cách khai thác các chức năng chính của ứng dụng (xem điểm, xem kết quả học tập, xem thông tin cá nhân, tra cứu quy chế...).

Nguyên tắc trả lời:
- Khi người dùng hỏi chung chung như “bạn có thể giúp tôi gì”, hãy liệt kê một cách ngắn gọn, rõ ràng các nhóm chức năng bạn hỗ trợ, tập trung vào:
- Giải thích quy chế, quy định đào tạo, học vụ.
- Hỗ trợ hiểu cấu trúc dữ liệu và thông tin có trong hệ thống (điểm số, kết quả học tập, thông tin sinh viên...).
- Gợi ý những kiểu câu hỏi mà người dùng có thể hỏi.
- Khi câu hỏi quá chi tiết về dữ liệu cá nhân (ví dụ điểm của một sinh viên cụ thể) thì hãy giải thích rằng bạn KHÔNG trực tiếp truy vấn dữ liệu thời gian thực, mà chỉ hỗ trợ giải thích quy định và cấu trúc hệ thống.
- Trả lời bằng cùng ngôn ngữ với câu hỏi (nếu người dùng dùng tiếng Việt thì trả lời tiếng Việt).
- Ưu tiên trả lời ngắn gọn, rõ ràng, có thể dùng bullet khi phù hợp.
";

    var generalResp = await geminiModel.GenerateAsync(new ChatRequest
    {
        Messages = new List<Message>
        {
            new(systemPrompt, MessageRole.System, string.Empty),
            Message.Human(question)
        }
    }, new ChatSettings { User = "general-mode", UseStreaming = false });

    Console.WriteLine("\nAssistant> " + (generalResp.LastMessageContent ?? "(no content)"));
}
else {

    var ctx   = string.Join("\n---\n", hits.Select(h =>
        $"[Source: {h.Metadata ?? "unknown"} | score={h.Score:F4}]\n{TrimForPrompt(h.Content, 1200)}"));
    Console.WriteLine("\n====== RAG CONTEXT DÙNG CHO CÂU HỎI NÀY ======\n");
    Console.WriteLine(ctx);
    Console.WriteLine("\n==============================================\n");
    
    // Build context with source grouping for better comprehension
    var groupedContext = GroupContextBySources(hits.Take(8).ToList());
    
    var prompt = $@"
BẠN LÀ CHUYÊN GIA TƯ VẤN HỌC VỤ của Trường Đại học Công nghệ Thông tin (UIT).

NHIỆM VỤ: Trả lời câu hỏi của sinh viên dựa trên các quy chế, quy định chính thức được cung cấp bên dưới.

NGUYÊN TẮC TRẢ LỜI:
1. CHỈ sử dụng thông tin từ CONTEXT bên dưới - KHÔNG được tự suy diễn hoặc thêm thông tin
2. Nếu CONTEXT chứa thông tin trực tiếp trả lời được câu hỏi → Trả lời đầy đủ, chính xác
3. Nếu CONTEXT chỉ có một phần thông tin → Trả lời phần có thể, ghi rõ ""phần này chưa được nêu trong tài liệu""
4. Nếu CONTEXT không có thông tin liên quan → Trả lời: ""Mình không có thông tin để trả lời câu hỏi này.""
5. Trích dẫn điều khoản cụ thể khi có (VD: ""Theo Điều 15..."")
6. Dùng bullet points cho danh sách điều kiện
7. Trả lời bằng tiếng Việt, văn phong thân thiện
10. Không cần phải đưa ra tên file tài liệu gốc mà nếu có thể hãy đoán tên tài liệu dựa trên tên file

CONTEXT (Trích từ quy chế đào tạo):
{groupedContext}

CÂU HỎI: {question}

TRẢ LỜI:";

    var resp = await geminiModel.GenerateAsync(new ChatRequest
    {
        Messages = new List<Message>
        {
            new(
                "Bạn là chuyên gia tư vấn học vụ. Chỉ trả lời dựa trên thông tin được cung cấp. " +
                "Không bao giờ bịa thông tin. Nếu không chắc chắn, hãy nói rõ.",
                MessageRole.System,
                string.Empty),
            Message.Human(prompt)
        }
    }, new ChatSettings { User = "db-rag", UseStreaming = false });

    Console.WriteLine("\nAssistant> " + (resp.LastMessageContent ?? "(no content)"));
    Console.WriteLine("\nDone.");
}

// Helper method to group context by source documents
static string GroupContextBySources(List<KbHit> hits)
{
    var grouped = hits
        .GroupBy(h => ExtractDocName(h.Metadata))
        .OrderByDescending(g => g.Max(h => h.Score));

    var sb = new System.Text.StringBuilder();
    foreach (var group in grouped)
    {
        sb.AppendLine($"\n📄 {group.Key}:");
        sb.AppendLine("─────────────────────");
        foreach (var hit in group.OrderByDescending(h => h.Score).Take(3))
        {
            sb.AppendLine(TrimForPrompt(hit.Content, 800));
            sb.AppendLine();
        }
    }
    return sb.ToString();
}

static string ExtractDocName(string? metadata)
{
    if (string.IsNullOrEmpty(metadata)) return "Không xác định";
    
    // Try to extract title from metadata
    var titleMatch = System.Text.RegularExpressions.Regex.Match(metadata, @"title:([^;]+)");
    if (titleMatch.Success)
        return titleMatch.Groups[1].Value.Trim();
    
    var docMatch = System.Text.RegularExpressions.Regex.Match(metadata, @"doc:([^;]+)");
    if (docMatch.Success)
        return docMatch.Groups[1].Value.Trim();
    
    return metadata.Split(';').FirstOrDefault() ?? "Không xác định";
}

static async Task<bool> HasKbDocsAsync(string cs, string table = "kb_docs")
{
    try
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"SELECT COUNT(*) FROM ""{table}"";", conn);
        var o = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(o) > 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not check existing vector store: {ex.Message}");
        return false;
    }
}

record Doc(string Id, string Content, string Tag);
record KbDoc(string Id, string Content, string? Metadata, float[] Embedding);
record DocxChunk(
    string DocumentId,
    int ChunkIndex,
    string SectionTitle,
    string Content
);
record KbHit(
    string Id,
    string Content,
    string? Metadata,
    double Score
);
