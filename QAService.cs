using LangChain.Providers;
using LangChain.Providers.Google;
using System.Text;

/// <summary>
/// Service for answering questions using RAG (Retrieval-Augmented Generation)
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
    /// Answer a question using RAG
    /// </summary>
    /// <param name="question">The question to answer</param>
    /// <param name="showContext">Whether to include context in the result</param>
    /// <returns>QAResult containing the answer, context used, and hit count</returns>
    public async Task<QAResult> AnswerQuestionAsync(string question, bool showContext = false)
    {
        var result = new QAResult { Question = question };

        try
        {
            // Embed the question
            var qVec = Program.Normalize(await Program.EmbedAsyncSingle(_apiKey, question, _httpClient, isQuery: true));
            
            // Hybrid search
            var hits = await Program.HybridSearchAsync(_connectionString, question, qVec, k: 10, table: "kb_docs");
            result.HitCount = hits.Count;

            if (hits.Count == 0)
            {
                result.Answer = await GetGeneralResponseAsync(question);
                result.HasContext = false;
            }
            else
            {
                // Save context
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

⚠️ HƯỚNG DẪN ĐỌC CONTEXT:
- ĐỌC KỸ TOÀN BỘ context trước khi trả lời, đặc biệt chú ý các CON SỐ CỤ THỂ (số tiết, số tín chỉ, điểm, thời gian...)
- Chú ý các BẢNG BIỂU có dạng ""Bảng X. ..."" hoặc liệt kê dữ liệu theo dòng - đây thường là câu trả lời trực tiếp
- Các quy định thường nằm trong ""Điều X. ..."" hoặc ""Khoản X..."" - trích dẫn chính xác
- Nếu câu hỏi về ĐIỀU KIỆN, tìm các từ khóa: ""nếu"", ""được phép"", ""phải"", ""tối thiểu"", ""tối đa""
- Nếu câu hỏi về THỜI HẠN, tìm các từ khóa: ""trong vòng"", ""trước"", ""sau"", ""chậm nhất""

NGUYÊN TẮC TRẢ LỜI:
1. CHỈ sử dụng thông tin từ CONTEXT - KHÔNG tự suy diễn
2. ƯU TIÊN trích xuất SỐ LIỆU CỤ THỂ: số tiết, số tín chỉ, điểm số, thời gian, mức điểm TOEIC/IELTS...
3. Nếu context có thông tin → PHẢI trả lời, kèm trích dẫn điều khoản (""Theo Điều X..."")
4. Giải thích các từ viết tắt nếu có trong context: I (chưa hoàn thành), M (miễn), BL (bảo lưu)...
5. Nếu context có nhiều trường hợp (VD: CTC, CTTT, CLC...) → liệt kê rõ từng trường hợp
6. Dùng bullet points cho danh sách điều kiện
7. Trả lời bằng tiếng Việt, văn phong thân thiện, ngắn gọn
8. CHỈ NÓI ""không có thông tin"" khi context THỰC SỰ không đề cập gì liên quan
9. Không liệt kê nguồn theo dạng tên file, mà tự dịch lại tên file thành tên tài liệu chính thức (VD: ""Quy chế đào tạo đại học chính quy"")

CONTEXT (Trích từ quy chế đào tạo hoặc cơ sở dữ liệu):
{context}

CÂU HỎI: {question}

TRẢ LỜI (nhớ trích xuất số liệu cụ thể nếu có):";

        var resp = await _geminiModel.GenerateAsync(new ChatRequest
        {
            Messages = new List<Message>
            {
                new(
                    "Bạn là chuyên gia tư vấn học vụ của Trường Đại học Công nghệ Thông tin. " +
                    "Bạn PHẢI trích xuất và trả lời dựa trên thông tin có trong context. " +
                    "Đặc biệt chú ý các con số cụ thể (số tiết, điểm, thời gian, mức TOEIC/IELTS...). " +
                    "Không được nói 'không có thông tin' nếu context có chứa câu trả lời.",
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
            // Lấy tối đa 4 hits từ mỗi source thay vì 3 để không bỏ sót thông tin
            foreach (var hit in group.OrderByDescending(h => h.Score).Take(4))
            {
                // Tăng limit lên 1000 ký tự để giữ nhiều context hơn
                sb.AppendLine(Program.TrimForPrompt(hit.Content, 1000));
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
/// Result of a question-answering operation
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
