using LangChain.Providers;
using LangChain.Providers.Google;
using System.Text;

/// <summary>
/// Service để trả lời câu hỏi sử dụng RAG
/// </summary>
public class QAService
{
    private readonly string _connectionString;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly GoogleChatModel _geminiModel;

    public QAService(string connectionString, string apiKey, HttpClient httpClient, GoogleChatModel geminiModel)
    {
        _connectionString = connectionString;
        _apiKey = apiKey;
        _httpClient = httpClient;
        _geminiModel = geminiModel;
    }

    /// <summary>
    /// Trả lời một câu hỏi sử dụng RAG
    /// </summary>
    /// <param name="question">Câu hỏi cần trả lời</param>
    /// <param name="showContext">Hiển thị context hay không</param>
    /// <returns>Tuple chứa (câu trả lời, context được sử dụng, số hits tìm được)</returns>
    public async Task<QAResult> AnswerQuestionAsync(string question, bool showContext = false)
    {
        var result = new QAResult { Question = question };

        try
        {
            // Embed câu hỏi
            var qVec = Program.Normalize(await Program.EmbedAsyncSingle(_apiKey, question, _httpClient, isQuery: true));
            
            // Tìm kiếm hybrid
            var hits = await Program.HybridSearchAsync(_connectionString, question, qVec, k: 10, table: "kb_docs");
            result.HitCount = hits.Count;

            if (hits.Count == 0)
            {
                result.Answer = await GetGeneralResponseAsync(question);
                result.HasContext = false;
            }
            else
            {
                // Lưu context
                if (showContext)
                {
                    result.Context = string.Join("\n---\n", hits.Select(h =>
                        $"[Source: {h.Metadata ?? "unknown"} | score={h.Score:F4}]\n{Program.TrimForPrompt(h.Content, 800)}"));
                }

                // Build context và tạo prompt
                var groupedContext = GroupContextBySources(hits.Take(8).ToList());
                result.Answer = await GetRAGResponseAsync(question, groupedContext);
                result.HasContext = true;
                result.TopScore = hits.Max(h => h.Score);
            }
        }
        catch (Exception ex)
        {
            result.Answer = $"Lỗi: {ex.Message}";
            result.Error = ex.Message;
        }

        return result;
    }

    private async Task<string> GetGeneralResponseAsync(string question)
    {
        var systemPrompt = @"
Bạn là trợ lý ảo tích hợp trong ứng dụng học vụ của Trường Đại học Công nghệ Thông Tin.

Vai trò của bạn:
- Hỗ trợ sinh viên, giảng viên và cán bộ hiểu và sử dụng ứng dụng học vụ.
- Giải thích các quy chế, quy định, quy trình liên quan đến đào tạo, học vụ, điểm số, kết quả học tập.

Nguyên tắc trả lời:
- Trả lời bằng tiếng Việt, văn phong thân thiện.
- Ưu tiên trả lời ngắn gọn, rõ ràng.
- Nếu không có thông tin, nói rõ là không có đủ dữ liệu.
";

        var resp = await _geminiModel.GenerateAsync(new ChatRequest
        {
            Messages = new List<Message>
            {
                new(systemPrompt, MessageRole.System, string.Empty),
                Message.Human(question)
            }
        }, new ChatSettings { User = "general-mode", UseStreaming = false });

        return resp.LastMessageContent ?? "(no content)";
    }

    private async Task<string> GetRAGResponseAsync(string question, string context)
    {
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
8. Trả lời ngắn gọn, súc tích, tập trung vào câu hỏi

CONTEXT (Trích từ quy chế đào tạo):
{context}

CÂU HỎI: {question}

TRẢ LỜI:";

        var resp = await _geminiModel.GenerateAsync(new ChatRequest
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

        return resp.LastMessageContent ?? "(no content)";
    }

    private static string GroupContextBySources(List<KbHit> hits)
    {
        var grouped = hits
            .GroupBy(h => ExtractDocName(h.Metadata))
            .OrderByDescending(g => g.Max(h => h.Score));

        var sb = new StringBuilder();
        foreach (var group in grouped)
        {
            sb.AppendLine($"\n📄 {group.Key}:");
            sb.AppendLine("─────────────────────");
            foreach (var hit in group.OrderByDescending(h => h.Score).Take(3))
            {
                sb.AppendLine(Program.TrimForPrompt(hit.Content, 800));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string ExtractDocName(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return "Không xác định";

        var titleMatch = System.Text.RegularExpressions.Regex.Match(metadata, @"title:([^;]+)");
        if (titleMatch.Success)
            return titleMatch.Groups[1].Value.Trim();

        var docMatch = System.Text.RegularExpressions.Regex.Match(metadata, @"doc:([^;]+)");
        if (docMatch.Success)
            return docMatch.Groups[1].Value.Trim();

        return metadata.Split(';').FirstOrDefault() ?? "Không xác định";
    }
}

/// <summary>
/// Kết quả trả lời câu hỏi
/// </summary>
public class QAResult
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string? Context { get; set; }
    public bool HasContext { get; set; }
    public int HitCount { get; set; }
    public double TopScore { get; set; }
    public string? Error { get; set; }
}
