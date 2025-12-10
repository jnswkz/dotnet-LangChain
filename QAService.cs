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
    /// <param name="userId">Optional userId/MSSV to query specific student data</param>
    /// <returns>QAResult containing the answer, context used, and hit count</returns>
    public async Task<QAResult> AnswerQuestionAsync(string question, bool showContext = false, string? userId = null)
    {
        var result = new QAResult { Question = question };

        try
        {
            // Embed the question
            var qVec = Program.Normalize(await Program.EmbedAsyncSingle(_apiKey, question, _httpClient, isQuery: true));
            
            // Search document embeddings (kb_docs)
            var docHits = await Program.HybridSearchAsync(_connectionString, question, qVec, k: 10, table: "kb_docs");
            
            var allHits = docHits.ToList();
            
            // If userId is provided, query database directly for student-specific data
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var studentData = await QueryStudentDataAsync(userId);
                if (!string.IsNullOrEmpty(studentData))
                {
                    // Add student data as a high-priority hit
                    allHits.Insert(0, new KbHit(
                        Id: $"student:{userId}",
                        Content: studentData,
                        Metadata: $"database:student_data;mssv:{userId}",
                        Score: 1.0  // Highest priority
                    ));
                }
            }
            
            result.HitCount = allHits.Count;

            if (allHits.Count == 0)
            {
                result.Answer = await GetGeneralResponseAsync(question);
                result.HasContext = false;
            }
            else
            {
                // Save context
                if (showContext)
                {
                    result.Context = string.Join("\n---\n", allHits.Select(h =>
                        $"[Source: {h.Metadata ?? "unknown"} | score={h.Score:F4}]\n{Program.TrimForPrompt(h.Content, 800)}"));
                }

                // Build context và tạo prompt
                var groupedContext = GroupContextBySources(allHits.Take(8).ToList());
                result.Answer = await GetRAGResponseAsync(question, groupedContext);
                result.HasContext = true;
                result.TopScore = allHits.Max(h => h.Score);
            }
        }
        catch (Exception ex)
        {
            result.Answer = $"Lỗi: {ex.Message}";
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Query student-specific data from database by MSSV
    /// </summary>
    private async Task<string> QueryStudentDataAsync(string mssv)
    {
        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            
            var sb = new StringBuilder();
            sb.AppendLine($"=== DỮ LIỆU SINH VIÊN MSSV: {mssv} ===");
            sb.AppendLine();

            // Query student info
            var studentSql = @"
                SELECT ho_ten, ngay_sinh, nganh_hoc, khoa_hoc, lop_sinh_hoat, email_ca_nhan
                FROM sinh_vien WHERE mssv = @mssv";
            await using (var cmd = new Npgsql.NpgsqlCommand(studentSql, conn))
            {
                cmd.Parameters.AddWithValue("@mssv", int.Parse(mssv));
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    sb.AppendLine($"Họ tên: {reader.GetString(0)}");
                    sb.AppendLine($"Ngày sinh: {reader.GetDateTime(1):dd/MM/yyyy}");
                    sb.AppendLine($"Ngành học: {reader.GetString(2)}");
                    sb.AppendLine($"Khóa học: {reader.GetString(3)}");
                    sb.AppendLine($"Lớp sinh hoạt: {reader.GetString(4)}");
                    sb.AppendLine($"Email: {reader.GetString(5)}");
                    sb.AppendLine();
                }
            }

            // Query registered courses (from ket_qua_hoc_tap)
            var coursesSql = @"
                SELECT DISTINCT k.ma_lop, m.ten_mon_hoc_vn, k.diem_qua_trinh, k.diem_giua_ki, 
                       k.diem_thuc_hanh, k.diem_cuoi_ki, k.ghi_chu
                FROM ket_qua_hoc_tap k
                JOIN mon_hoc m ON k.ma_lop_goc = m.ma_mon_hoc
                WHERE k.mssv = @mssv
                ORDER BY k.ma_lop";
            await using (var cmd = new Npgsql.NpgsqlCommand(coursesSql, conn))
            {
                cmd.Parameters.AddWithValue("@mssv", int.Parse(mssv));
                await using var reader = await cmd.ExecuteReaderAsync();
                if (reader.HasRows)
                {
                    sb.AppendLine("DANH SÁCH MÔN HỌC ĐÃ ĐĂNG KÝ:");
                    while (await reader.ReadAsync())
                    {
                        sb.AppendLine($"- Lớp: {reader.GetString(0)} - {reader.GetString(1)}");
                        if (!reader.IsDBNull(2)) sb.AppendLine($"  Điểm QT: {reader.GetDecimal(2)}");
                        if (!reader.IsDBNull(3)) sb.AppendLine($"  Điểm GK: {reader.GetDecimal(3)}");
                        if (!reader.IsDBNull(4)) sb.AppendLine($"  Điểm TH: {reader.GetDecimal(4)}");
                        if (!reader.IsDBNull(5)) sb.AppendLine($"  Điểm CK: {reader.GetDecimal(5)}");
                        if (!reader.IsDBNull(6)) sb.AppendLine($"  Ghi chú: {reader.GetString(6)}");
                    }
                    sb.AppendLine();
                }
            }

            // Query tuition fees
            var tuitionSql = @"
                SELECT hoc_ky, so_tin_chi, hoc_phi, no_hoc_ky_truoc, da_dong, so_tien_con_lai
                FROM hoc_phi WHERE mssv = @mssv
                ORDER BY hoc_ky DESC LIMIT 5";
            await using (var cmd = new Npgsql.NpgsqlCommand(tuitionSql, conn))
            {
                cmd.Parameters.AddWithValue("@mssv", int.Parse(mssv));
                await using var reader = await cmd.ExecuteReaderAsync();
                if (reader.HasRows)
                {
                    sb.AppendLine("THÔNG TIN HỌC PHÍ:");
                    while (await reader.ReadAsync())
                    {
                        sb.AppendLine($"- Học kỳ: {reader.GetString(0)}");
                        sb.AppendLine($"  Số tín chỉ: {(!reader.IsDBNull(1) ? reader.GetInt32(1).ToString() : "N/A")}");
                        sb.AppendLine($"  Học phí: {(!reader.IsDBNull(2) ? reader.GetDecimal(2).ToString("N0") : "N/A")} VNĐ");
                        sb.AppendLine($"  Còn lại: {(!reader.IsDBNull(5) ? reader.GetDouble(5).ToString("N0") : "N/A")} VNĐ");
                    }
                    sb.AppendLine();
                }
            }

            // Query language certificates
            var certSql = @"
                SELECT loai_chung_chi, diem_so, ngay_cap, trang_thai
                FROM chung_chi_ngoai_ngu WHERE mssv = @mssv";
            await using (var cmd = new Npgsql.NpgsqlCommand(certSql, conn))
            {
                cmd.Parameters.AddWithValue("@mssv", int.Parse(mssv));
                await using var reader = await cmd.ExecuteReaderAsync();
                if (reader.HasRows)
                {
                    sb.AppendLine("CHỨNG CHỈ NGOẠI NGỮ:");
                    while (await reader.ReadAsync())
                    {
                        sb.AppendLine($"- {reader.GetString(0)}: {(!reader.IsDBNull(1) ? reader.GetString(1) : "N/A")}");
                        if (!reader.IsDBNull(2)) sb.AppendLine($"  Ngày cấp: {reader.GetDateTime(2):dd/MM/yyyy}");
                        if (!reader.IsDBNull(3)) sb.AppendLine($"  Trạng thái: {reader.GetString(3)}");
                    }
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Không thể truy vấn dữ liệu sinh viên MSSV {mssv}: {ex.Message}";
        }
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

NHIỆM VỤ: Trả lời câu hỏi của sinh viên dựa trên:
1. Quy chế, quy định chính thức (từ văn bản .docx)
2. Dữ liệu thực tế trong hệ thống (từ cơ sở dữ liệu)

⚠️ HƯỚNG DẪN ĐỌC CONTEXT:
- Context có 2 LOẠI NGUỒN:
  * 📄 Tài liệu: Quy chế, quy định chính thức
  * 🗄️ Dữ liệu: Thông tin từ bảng dữ liệu (bảng điểm, môn học, lịch học, lịch thi...)
- ĐỌC KỸ TOÀN BỘ context trước khi trả lời, đặc biệt chú ý các CON SỐ CỤ THỂ
- Với DỮ LIỆU từ database: Đây là thông tin THỰC TẾ (ví dụ: điểm của sinh viên, danh sách môn học...)
- Với TÀI LIỆU: Chú ý ""Điều X"", ""Khoản X"", ""Bảng X"" - trích dẫn chính xác
- Nếu câu hỏi về ĐIỀU KIỆN, tìm: ""nếu"", ""được phép"", ""phải"", ""tối thiểu"", ""tối đa""
- Nếu câu hỏi về THỜI HẠN, tìm: ""trong vòng"", ""trước"", ""sau"", ""chậm nhất""

NGUYÊN TẮC TRẢ LỜI:
1. CHỈ sử dụng thông tin từ CONTEXT - KHÔNG tự suy diễn
2. ƯU TIÊN trích xuất SỐ LIỆU CỤ THỂ: số tiết, số tín chỉ, điểm số, thời gian, mức điểm TOEIC/IELTS...
3. Nếu context từ DATABASE → trả lời dựa trên dữ liệu thực tế
4. Nếu context từ DOCUMENT → trích dẫn điều khoản (""Theo Điều X..."")
5. Giải thích các từ viết tắt: I (chưa hoàn thành), M (miễn), BL (bảo lưu)...
6. ⚠️ QUAN TRỌNG - LỌC THÔNG TIN THEO CHƯƠNG TRÌNH CỤ THỂ:
   - Nếu hỏi về ""Việt Nhật"" / ""Nhật Bản"" / ""CLC Nhật"" → CHỈ trả lời chứng chỉ TIẾNG NHẬT (JLPT, NAT-TEST)
   - Nếu hỏi về ""CTTT"" / ""Tiên tiến"" → CHỈ trả lời chứng chỉ cho CTTT
   - Nếu hỏi về ""CTC"" / ""Chuẩn"" → CHỈ trả lời chứng chỉ cho CTC
   - KHÔNG liệt kê tất cả các loại chứng chỉ nếu câu hỏi chỉ hỏi về 1 chương trình cụ thể
7. Nếu có nhiều trường hợp MÀ câu hỏi không chỉ định cụ thể → liệt kê rõ từng trường hợp
8. Dùng bullet points cho danh sách
9. Trả lời bằng tiếng Việt, văn phong thân thiện, ngắn gọn
10. CHỈ NÓI ""không có thông tin"" khi context THỰC SỰ không đề cập gì liên quan

CONTEXT (Từ tài liệu quy chế VÀ cơ sở dữ liệu):
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
            .GroupBy(h => new { 
                Name = ExtractDocName(h.Metadata),
                IsDatabase = IsFromDatabase(h.Metadata)
            })
            .OrderByDescending(g => g.Max(h => h.Score));

        var sb = new StringBuilder();
        foreach (var group in grouped)
        {
            // Use different icons for document vs database sources
            var icon = group.Key.IsDatabase ? "🗄️" : "📄";
            var sourceType = group.Key.IsDatabase ? "[Dữ liệu DB]" : "[Tài liệu]";
            
            sb.AppendLine($"\n{icon} {sourceType} {group.Key.Name}:");
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
    
    private static bool IsFromDatabase(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return false;
        // Check if metadata indicates it's from database
        return metadata.Contains("table:") || 
               metadata.Contains("schema:") || 
               metadata.ToLower().Contains("database");
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
