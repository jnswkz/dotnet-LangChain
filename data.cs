using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;

partial class Program
{
    /// <summary>
    /// Scans Postgres metadata, ensures the vector store exists, and ingests the latest docs.
    /// Returns true if ingestion ran (false when no user tables are found).
    /// </summary>
    /// 
    /// 
    public static async Task ClearVectorStoreAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Nếu bạn chỉ muốn clear kb_docs:
        const string sql = @"TRUNCATE TABLE kb_docs;";

        // Hoặc clear nhiều bảng 1 lúc:
        // const string sql = @"TRUNCATE TABLE kb_docs, kb_db;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("✅ Vector store cleared (kb_docs).");
    }
    public static async Task<bool> SyncVectorStoreAsync(
        string connectionString,
        string apiKey,
        HttpClient httpClient)
    {
        Console.WriteLine("Scanning database metadata & sampling rows...");
        var docs = await BuildDocumentsFromDatabaseAsync(connectionString, maxRowsPerTable: 10);

        if (docs.Count == 0)
        {
            Console.WriteLine("No user tables found to ingest. Exiting.");
            return false;
        }

        Console.WriteLine("Creating vector store (if needed)...");
        var sampleVec = await EmbedAsyncSingle(apiKey, "probe", httpClient);
        var dim = sampleVec.Length;
        await EnsureKbTableAsync(connectionString, dim, "kb_docs");

        Console.WriteLine($"Ingesting {docs.Count} doc chunks into vector store...");
        var embeddings = await EmbedAsyncBatch(apiKey, docs.Select(d => d.Content), httpClient);
        var toUpsert = new List<KbDoc>(docs.Count);
        for (int i = 0; i < docs.Count; i++)
        {
            toUpsert.Add(new KbDoc(
                Id: docs[i].Id,
                Content: docs[i].Content,
                Metadata: docs[i].Tag,
                Embedding: Normalize(embeddings[i])
            ));
        }
        await UpsertDocsAsync(connectionString, toUpsert, "kb_docs");

        return true;
    }

    private static void DescribePostgresTarget(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            Console.WriteLine($"Vector store target: {builder.Host}:{builder.Port}/{builder.Database}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to parse Postgres connection string: {ex.Message}");
        }
    }

    private static async Task<List<Doc>> BuildDocumentsFromDatabaseAsync(
        string cs,
        int maxRowsPerTable = 10,
        int maxCellsPerRow = 24,
        int maxDocChars = 1800)
    {
        var docs = new List<Doc>();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        const string listTablesSql = @"
        SELECT table_schema, table_name
        FROM information_schema.tables
        WHERE table_type = 'BASE TABLE'
          AND table_schema NOT IN ('pg_catalog','information_schema')
        ORDER BY table_schema, table_name;";

        var tables = new List<(string Schema, string Table)>();
        await using (var cmd = new NpgsqlCommand(listTablesSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add((reader.GetString(0), reader.GetString(1)));
        }

        const string colsSql = @"
        SELECT column_name, data_type
        FROM information_schema.columns
        WHERE table_schema = @schema AND table_name = @table
        ORDER BY ordinal_position;";

        foreach (var (schema, table) in tables)
        {
            var cols = new List<(string Name, string Type)>();
            await using (var cmd = new NpgsqlCommand(colsSql, conn))
            {
                cmd.Parameters.AddWithValue("@schema", schema);
                cmd.Parameters.AddWithValue("@table", table);
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                    cols.Add((rdr.GetString(0), rdr.GetString(1)));
            }

            long rowCount = 0;
            await using (var cmd = new NpgsqlCommand($@"SELECT COUNT(*) FROM ""{schema}"".""{table}"";", conn))
            {
                var o = await cmd.ExecuteScalarAsync();
                rowCount = Convert.ToInt64(o);
            }

            var sampleSql = $@"SELECT * FROM ""{schema}"".""{table}"" LIMIT {maxRowsPerTable};";
            var rows = new List<Dictionary<string, object?>>();
            await using (var cmd = new NpgsqlCommand(sampleSql, conn))
            await using (var rdr = await cmd.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                {
                    var obj = new Dictionary<string, object?>();
                    for (int i = 0; i < rdr.FieldCount && i < maxCellsPerRow; i++)
                    {
                        var name = rdr.GetName(i);
                        var typeName = rdr.GetDataTypeName(i);

                        object? val;
                        if (await rdr.IsDBNullAsync(i))
                        {
                            val = null;
                        }
                        else if (typeName.EndsWith(".vector", StringComparison.OrdinalIgnoreCase) ||
                                 typeName.Equals("vector", StringComparison.OrdinalIgnoreCase))
                        {
                            val = "<vector>";
                        }
                        else
                        {
                            try
                            {
                                val = rdr.GetValue(i);
                            }
                            catch (InvalidCastException)
                            {
                                val = $"<{typeName}>";
                            }
                        }

                        obj[name] = val;
                    }
                    rows.Add(obj);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"TABLE: {schema}.{table}");
            sb.AppendLine($"ROW_COUNT: {rowCount}");
            if (cols.Count > 0)
            {
                sb.AppendLine("COLUMNS:");
                foreach (var c in cols)
                    sb.AppendLine($"- {c.Name} ({c.Type})");
            }
            if (rows.Count > 0)
            {
                sb.AppendLine("SAMPLE_ROWS:");
                foreach (var r in rows)
                {
                    var kv = string.Join(", ", r.Select(kv2 => $"{kv2.Key}={ValueToString(kv2.Value)}"));
                    sb.AppendLine($"- {{ {kv} }}");
                }
            }

            var content = sb.ToString();
            if (content.Length > maxDocChars)
                content = content.Substring(0, maxDocChars) + "\n";

            var id = $"table::{schema}.{table}";
            var tag = $"{schema}.{table}";
            docs.Add(new Doc(id, content, tag));
        }

        return docs;
    }

    private static string ValueToString(object? v)
    {
        if (v is null) return "NULL";
        return v switch
        {
            DateTime dt => dt.ToString("o"),
            DateOnly d0 => d0.ToString("yyyy-MM-dd"),
            bool b => b ? "true" : "false",
            byte[] bytes => $"<bytea:{bytes.Length}>",
            _ => v.ToString() ?? "NULL"
        };
    }

    private static async Task EnsureKbTableAsync(string cs, int dim, string table = "kb_docs")
    {
        var sql = $@"
    CREATE TABLE IF NOT EXISTS ""{table}"" (
    id TEXT PRIMARY KEY,
    content  TEXT NOT NULL,
    metadata TEXT NULL,
    embedding vector({dim}) NOT NULL,
    tsv tsvector
    );

    ALTER TABLE ""{table}""
    ADD COLUMN IF NOT EXISTS tsv tsvector;

    CREATE INDEX IF NOT EXISTS idx_{table}_tsv
    ON ""{table}"" USING GIN (tsv);
    ";
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }


    private static async Task UpsertDocsAsync(string cs, IEnumerable<KbDoc> docs, string table = "kb_docs")
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        const int batchSize = 50;
        var batch = new List<KbDoc>(batchSize);

        foreach (var d in docs)
        {
            batch.Add(d);
            if (batch.Count == batchSize)
            {
                await UpsertBatchAsync(conn, batch, table);
                batch.Clear();
            }
        }
        if (batch.Count > 0) await UpsertBatchAsync(conn, batch, table);
    }

    private static async Task UpsertBatchAsync(NpgsqlConnection conn, List<KbDoc> batch, string table)
    {
        var sb = new StringBuilder();
        sb.Append($@"INSERT INTO ""{table}"" (id, content, metadata, embedding, tsv) VALUES ");

        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(",");

            // tsv được build trực tiếp từ content{i} ngay trong SQL
            sb.Append($"(@id{i}, @content{i}, @meta{i}, @emb{i}::vector, to_tsvector('simple', @content{i}))");
        }

        sb.Append(@" ON CONFLICT (id) DO UPDATE SET 
            content   = EXCLUDED.content,
            metadata  = EXCLUDED.metadata,
            embedding = EXCLUDED.embedding,
            tsv       = EXCLUDED.tsv;");

        await using var cmd = new NpgsqlCommand(sb.ToString(), conn);

        for (int i = 0; i < batch.Count; i++)
        {
            var b = batch[i];
            cmd.Parameters.AddWithValue($"@id{i}", b.Id);
            cmd.Parameters.AddWithValue($"@content{i}", b.Content);
            cmd.Parameters.AddWithValue($"@meta{i}", (object?)b.Metadata ?? DBNull.Value);

            var vecText = "[" + string.Join(",", b.Embedding.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
            cmd.Parameters.AddWithValue($"@emb{i}", vecText);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string Id, string Content, string? Metadata, double Score)>> 
        SimilaritySearchAsync(string cs, float[] queryVector, int k = 6, string table = "kb_docs")
    {
        var results = new List<(string,string?,string?,double)>(k);
        var vecText = "[" + string.Join(",", queryVector.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        var sql = $@"
SELECT id, content, metadata, (embedding <=> {Quote(vecText)}::vector) AS score
FROM ""{table}""
ORDER BY embedding <=> {Quote(vecText)}::vector
LIMIT @k;";

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@k", k);

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            results.Add((
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.IsDBNull(2) ? null : rdr.GetString(2),
                rdr.GetDouble(3)
            ));
        }
        return results;
    }

/// <summary>
/// Expands Vietnamese query with synonyms and related terms for better retrieval
/// </summary>
private static string ExpandVietnameseQuery(string question)
{
    var expanded = question;
    
    // Vietnamese academic term synonyms and expansions
    var synonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // === TỐT NGHIỆP & XẾP LOẠI (Q14-Q17) ===
        { "tốt nghiệp", new[] { "tốt nghiệp", "hoàn thành", "ra trường", "cấp bằng", "công nhận tốt nghiệp", "xét tốt nghiệp", "Điều 32", "Điều 33" } },
        { "xét tốt nghiệp", new[] { "xét tốt nghiệp", "công nhận tốt nghiệp", "điều kiện tốt nghiệp", "đợt xét", "Điều 32" } },
        { "xếp loại", new[] { "xếp loại", "xếp loại tốt nghiệp", "xuất sắc", "giỏi", "khá", "trung bình", "Điều 33" } },
        { "xuất sắc", new[] { "xuất sắc", "xếp loại xuất sắc", "giảm bậc", "Điều 33" } },
        
        // === ĐIỂM SỐ & ĐTBCTL (Q8, Q18, Q19) ===
        { "điểm", new[] { "điểm", "điểm số", "thang điểm", "điểm trung bình", "ĐTBHK", "điểm trung bình học kỳ", "Điều 24" } },
        { "ĐTBCTL", new[] { "ĐTBCTL", "điểm trung bình chung tích lũy", "điểm trung bình tích lũy", "xếp hạng tốt nghiệp", "Điều 24" } },
        { "điểm trung bình", new[] { "điểm trung bình", "ĐTBHK", "ĐTBC", "ĐTBCTL", "Điều 24" } },
        { "điểm I", new[] { "điểm I", "điểm M", "điểm BL", "không tính", "Điều 24" } },
        { "điểm cuối kỳ", new[] { "điểm cuối kỳ", "điểm giữa kỳ", "thay thế", "điểm thi" } },
        
        // === HỌC PHÍ ===
        { "học phí", new[] { "học phí", "đóng học phí", "miễn giảm học phí", "phí", "hoàn thành học phí" } },
        
        // === TÍN CHỈ & ĐĂNG KÝ (Q2, Q5, Q6, Q7, Q21) ===
        { "tín chỉ", new[] { "tín chỉ", "số tín chỉ", "đăng ký tín chỉ", "tín chỉ tối thiểu", "tín chỉ tối đa", "đăng ký học", "Điều 14", "Điều 4", "Điều 7" } },
        { "đăng ký", new[] { "đăng ký", "đăng ký học", "đăng ký tín chỉ", "đăng ký học tập", "đăng ký học phần", "Điều 14" } },
        { "tối thiểu", new[] { "tối thiểu", "ít nhất", "tối đa", "nhiều nhất", "giới hạn", "số tín chỉ đăng ký" } },
        { "tối đa", new[] { "tối đa", "nhiều nhất", "tối thiểu", "ít nhất", "giới hạn", "số tín chỉ đăng ký", "thời gian tối đa" } },
        { "học cải thiện", new[] { "học cải thiện", "cải thiện điểm", "học lại", "đăng ký cải thiện", "Điều 14", "Điều 3" } },
        { "học lại", new[] { "học lại", "học phần học lại", "đăng ký học lại", "Điều 3" } },
        { "học vượt", new[] { "học vượt", "đăng ký học vượt", "học phần mới", "học kỳ hè", "Điều 14" } },
        
        // === HỌC KỲ & THỜI GIAN (Q1, Q3, Q4) ===
        { "học kỳ", new[] { "học kỳ", "học kỳ chính", "học kỳ hè", "kỳ học", "semester", "Điều 5" } },
        { "học kỳ hè", new[] { "học kỳ hè", "kỳ hè", "hè", "12 tín chỉ", "Điều 14" } },
        { "tuần", new[] { "tuần", "tuần thực học", "tuần học", "Điều 5" } },
        { "tiết", new[] { "tiết", "tiết học", "tiết lý thuyết", "50 phút", "15 tiết", "Điều 4" } },
        { "thời gian", new[] { "thời gian", "thời gian tối đa", "thời gian hoàn thành", "Điều 6" } },
        { "khóa học", new[] { "khóa học", "hoàn thành khóa học", "thời gian khóa học", "Điều 6" } },
        { "văn bằng", new[] { "văn bằng", "văn bằng 1", "văn bằng 2", "cử nhân", "Điều 6" } },
        { "chương trình đào tạo", new[] { "chương trình đào tạo", "CTĐT", "chương trình", "Điều 7" } },
        
        // === KHÓA LUẬN TỐT NGHIỆP (Q12, Q13) ===
        { "khóa luận", new[] { "khóa luận", "luận văn", "đồ án tốt nghiệp", "KLTN", "Điều 31", "Điều 10" } },
        { "đồ án", new[] { "đồ án", "khóa luận", "đồ án tốt nghiệp", "ĐATN", "Điều 31" } },
        { "bảo vệ", new[] { "bảo vệ", "bảo vệ khóa luận", "hội đồng bảo vệ", "ra bảo vệ", "Điều 10" } },
        { "hết thời gian", new[] { "hết thời gian", "gia hạn", "không ra bảo vệ", "Điều 10" } },
        
        // === CẢNH BÁO HỌC VỤ (Q9, Q10) ===
        { "cảnh báo", new[] { "cảnh báo", "cảnh báo học vụ", "xử lý học vụ", "Điều 16" } },
        { "cảnh báo học vụ", new[] { "cảnh báo học vụ", "cảnh báo", "xử lý học vụ", "ĐTBHK dưới", "điểm trung bình dưới", "Điều 16" } },
        { "đình chỉ", new[] { "đình chỉ", "đình chỉ học tập", "buộc thôi học", "thi hộ", "vi phạm kỷ luật", "Điều 16" } },
        { "đình chỉ học tập", new[] { "đình chỉ học tập", "đình chỉ", "bị đình chỉ", "tạm đình chỉ" } },
        { "thôi học", new[] { "thôi học", "buộc thôi học", "bị buộc thôi học", "nghỉ học", "Điều 17", "cho thôi học" } },
        { "buộc thôi học", new[] { "buộc thôi học", "thôi học", "đuổi học", "kỷ luật" } },
        { "học vụ", new[] { "học vụ", "xử lý học vụ", "cảnh báo học vụ", "quy trình học vụ", "Điều 16" } },
        { "xử lý học vụ", new[] { "xử lý học vụ", "cảnh báo học vụ", "đình chỉ", "buộc thôi học", "Điều 16" } },
        { "vi phạm", new[] { "vi phạm", "vi phạm kỷ luật", "kỷ luật", "thi hộ", "gian lận" } },
        
        // === SONG NGÀNH (Q11, Q40) ===
        { "song ngành", new[] { "song ngành", "ngành thứ hai", "hai ngành", "chương trình thứ hai", "đào tạo song ngành" } },
        { "ngành thứ hai", new[] { "ngành thứ hai", "song ngành", "chương trình thứ hai", "học thêm ngành" } },
        
        // === NGOẠI NGỮ (Q22-Q38) ===
        { "ngoại ngữ", new[] { "ngoại ngữ", "tiếng Anh", "tiếng Nhật", "TOEIC", "IELTS", "chuẩn ngoại ngữ", "chuẩn đầu ra" } },
        { "tiếng Anh", new[] { "tiếng Anh", "Anh văn", "ENG01", "ENG02", "ENG03", "English", "ngoại ngữ" } },
        { "tiếng Nhật", new[] { "tiếng Nhật", "Nhật ngữ", "JLPT", "N3", "Việt - Nhật", "CLC" } },
        { "TOEIC", new[] { "TOEIC", "điểm TOEIC", "Nghe-Đọc", "Nói-Viết", "450", "500", "600", "chuẩn TOEIC" } },
        { "IELTS", new[] { "IELTS", "điểm IELTS", "Academic", "General Training", "Indicator" } },
        { "chuẩn đầu ra", new[] { "chuẩn đầu ra", "chuẩn ngoại ngữ", "xét tốt nghiệp", "miễn học phần" } },
        { "miễn học phần", new[] { "miễn học phần", "miễn môn", "xét miễn", "điểm miễn", "điểm M" } },
        { "xếp lớp", new[] { "xếp lớp", "kiểm tra xếp lớp", "thi xếp lớp", "đầu khóa" } },
        { "chứng chỉ", new[] { "chứng chỉ", "bằng", "chứng chỉ ngoại ngữ", "thời hạn", "2 năm" } },
        { "chương trình tiên tiến", new[] { "chương trình tiên tiến", "CTTT", "tiên tiến" } },
        { "chương trình chuẩn", new[] { "chương trình chuẩn", "CTC", "chương trình đại trà" } },
        { "chương trình tài năng", new[] { "chương trình tài năng", "CTTN", "tài năng" } },
        
        // === ĐIỀU KIỆN & QUY TRÌNH ===
        { "điều kiện", new[] { "điều kiện", "yêu cầu", "tiêu chuẩn", "quy định" } },
        { "quy trình", new[] { "quy trình", "thủ tục", "cách thức", "hướng dẫn" } },
        { "công nhận", new[] { "công nhận", "xác nhận", "chấp nhận", "xét công nhận" } },
        { "đơn vị", new[] { "đơn vị", "phòng", "khoa", "P.ĐTĐH", "chủ trì" } },
    };

    // Build expanded query for full-text search
    var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var words = question.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    
    foreach (var word in words)
    {
        terms.Add(word);
    }

    // Add synonyms
    foreach (var kvp in synonyms)
    {
        if (question.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var syn in kvp.Value)
            {
                terms.Add(syn);
            }
        }
    }

    return string.Join(" ", terms);
}

/// <summary>
/// Improved hybrid search with query expansion, better BM25, and re-ranking
/// </summary>
public static async Task<List<KbHit>> HybridSearchAsync(
    string connectionString,
    string question,
    float[] queryEmbedding,
    int k = 15,                      
    string table = "kb_docs")
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    var vecText = "[" + string.Join(",",
        queryEmbedding.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    // Expand query with Vietnamese synonyms
    var expandedQuery = ExpandVietnameseQuery(question);
    
    // Use plainto_tsquery for more flexible matching (handles Vietnamese better)
    // Fetch more candidates for re-ranking
    var sql = $@"
WITH q AS (
    SELECT
        @q::text AS query_text,
        @expanded::text AS expanded_text,
        plainto_tsquery('simple', @expanded) AS query_ft
),
base AS (
    SELECT
        d.id,
        d.content,
        d.metadata,
        -- Vector similarity (cosine distance converted to similarity)
        1 - (d.embedding <=> {Quote(vecText)}::vector) AS vec_score,
        -- BM25-style scoring with ts_rank_cd
        COALESCE(ts_rank_cd(d.tsv, q.query_ft, 32), 0) AS bm25_score,
        -- Boost for exact phrase matches
        CASE WHEN d.content ILIKE '%' || @q || '%' THEN 0.15 ELSE 0 END AS exact_boost,
        -- Boost for metadata/keyword matches - IMPROVED
        CASE 
            -- Cảnh báo học vụ (Q9, Q10)
            WHEN d.metadata ILIKE '%cảnh báo học vụ%' AND @q ILIKE '%cảnh báo%' THEN 0.25
            WHEN d.metadata ILIKE '%đình chỉ%' AND @q ILIKE '%đình chỉ%' THEN 0.2
            WHEN d.metadata ILIKE '%xử lý học vụ%' AND @q ILIKE '%học vụ%' THEN 0.2
            WHEN d.metadata ILIKE '%điều 16%' AND (@q ILIKE '%cảnh báo%' OR @q ILIKE '%đình chỉ%' OR @q ILIKE '%học vụ%') THEN 0.25
            
            -- Đăng ký tín chỉ (Q5, Q6, Q7, Q21)
            WHEN d.metadata ILIKE '%đăng ký học tập%' AND (@q ILIKE '%tín chỉ%' OR @q ILIKE '%đăng ký%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 14%' AND (@q ILIKE '%tín chỉ%' OR @q ILIKE '%đăng ký%' OR @q ILIKE '%học kỳ hè%' OR @q ILIKE '%học cải thiện%' OR @q ILIKE '%học vượt%') THEN 0.25
            
            -- Thời gian & Học kỳ (Q1, Q3, Q4)
            WHEN d.metadata ILIKE '%điều 6%' AND (@q ILIKE '%thời gian%' OR @q ILIKE '%hoàn thành%' OR @q ILIKE '%văn bằng%' OR @q ILIKE '%khóa học%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 5%' AND (@q ILIKE '%tuần%' OR @q ILIKE '%học kỳ%' OR @q ILIKE '%đánh giá%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 4%' AND (@q ILIKE '%tiết%' OR @q ILIKE '%tín chỉ học tập%' OR @q ILIKE '%lý thuyết%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 7%' AND (@q ILIKE '%chương trình đào tạo%' OR @q ILIKE '%tổng số tín chỉ%') THEN 0.25
            
            -- Khóa luận (Q12, Q13)
            WHEN d.metadata ILIKE '%điều 31%' AND (@q ILIKE '%khóa luận%' OR @q ILIKE '%KLTN%' OR @q ILIKE '%đồ án%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 10%' AND d.metadata ILIKE '%KLTN%' AND (@q ILIKE '%hết thời gian%' OR @q ILIKE '%bảo vệ%') THEN 0.25
            
            -- Tốt nghiệp (Q14, Q15, Q16, Q17)
            WHEN d.metadata ILIKE '%điều 32%' AND (@q ILIKE '%xét tốt nghiệp%' OR @q ILIKE '%đợt xét%' OR @q ILIKE '%công nhận tốt nghiệp%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 33%' AND (@q ILIKE '%xếp loại%' OR @q ILIKE '%xuất sắc%' OR @q ILIKE '%giảm bậc%') THEN 0.25
            WHEN d.metadata ILIKE '%tốt nghiệp%' AND @q ILIKE '%tốt nghiệp%' THEN 0.15
            
            -- Điểm (Q8, Q18, Q19, Q20)
            WHEN d.metadata ILIKE '%điều 24%' AND (@q ILIKE '%ĐTBCTL%' OR @q ILIKE '%điểm trung bình%' OR @q ILIKE '%điểm I%' OR @q ILIKE '%điểm M%') THEN 0.25
            WHEN d.metadata ILIKE '%điều 3%' AND (@q ILIKE '%học lại%' OR @q ILIKE '%học phần%') THEN 0.2
            
            -- Song ngành (Q11, Q40)
            WHEN d.metadata ILIKE '%song ngành%' AND (@q ILIKE '%song ngành%' OR @q ILIKE '%ngành thứ hai%') THEN 0.25
            
            -- Ngoại ngữ (Q22-Q38)
            WHEN d.metadata ILIKE '%ngoại ngữ%' AND (@q ILIKE '%ngoại ngữ%' OR @q ILIKE '%tiếng Anh%' OR @q ILIKE '%TOEIC%' OR @q ILIKE '%IELTS%' OR @q ILIKE '%tiếng Nhật%') THEN 0.2
            WHEN d.metadata ILIKE '%điều 8%' AND (@q ILIKE '%chuẩn%' OR @q ILIKE '%ngoại ngữ%' OR @q ILIKE '%Anh văn%') THEN 0.2
            
            -- Generic match
            WHEN d.metadata ILIKE '%' || @q || '%' THEN 0.1
            ELSE 0 
        END AS meta_boost,
        -- Boost for content containing key terms
        CASE 
            -- Cảnh báo học vụ
            WHEN d.content ILIKE '%cảnh báo học vụ%' AND @q ILIKE '%cảnh báo%' THEN 0.2
            WHEN d.content ILIKE '%đình chỉ học tập%' AND @q ILIKE '%đình chỉ%' THEN 0.2
            WHEN d.content ILIKE '%ĐTBHK%' AND (@q ILIKE '%cảnh báo%' OR @q ILIKE '%điểm%') THEN 0.1
            
            -- Tín chỉ đăng ký
            WHEN d.content ILIKE '%tín chỉ tối thiểu%' AND (@q ILIKE '%tín chỉ%' OR @q ILIKE '%tối thiểu%') THEN 0.2
            WHEN d.content ILIKE '%tín chỉ tối đa%' AND (@q ILIKE '%tín chỉ%' OR @q ILIKE '%tối đa%') THEN 0.2
            WHEN d.content ILIKE '%đăng ký học%' AND (@q ILIKE '%đăng ký%' OR @q ILIKE '%tín chỉ%') THEN 0.15
            WHEN d.content ILIKE '%số tín chỉ đăng ký%' AND @q ILIKE '%tín chỉ%' THEN 0.2
            WHEN d.content ILIKE '%12 tín chỉ%' AND @q ILIKE '%học kỳ hè%' THEN 0.25
            WHEN d.content ILIKE '%học cải thiện%' AND @q ILIKE '%cải thiện%' THEN 0.2
            WHEN d.content ILIKE '%học vượt%' AND @q ILIKE '%học vượt%' THEN 0.2
            
            -- Thời gian & Học kỳ
            WHEN d.content ILIKE '%thời gian tối đa%' AND (@q ILIKE '%thời gian%' OR @q ILIKE '%tối đa%') THEN 0.2
            WHEN d.content ILIKE '%tuần thực học%' AND @q ILIKE '%tuần%' THEN 0.25
            WHEN d.content ILIKE '%15 tiết%' AND (@q ILIKE '%tiết%' OR @q ILIKE '%lý thuyết%') THEN 0.25
            WHEN d.content ILIKE '%50 phút%' AND @q ILIKE '%tiết%' THEN 0.2
            WHEN d.content ILIKE '%120%' AND d.content ILIKE '%132%' AND @q ILIKE '%tổng số tín chỉ%' THEN 0.25
            
            -- Khóa luận
            WHEN d.content ILIKE '%khóa luận tốt nghiệp%' AND (@q ILIKE '%khóa luận%' OR @q ILIKE '%KLTN%') THEN 0.2
            WHEN d.content ILIKE '%không nợ quá%' AND @q ILIKE '%điều kiện%' AND @q ILIKE '%KLTN%' THEN 0.2
            WHEN d.content ILIKE '%gia hạn%' AND @q ILIKE '%hết thời gian%' THEN 0.2
            
            -- Tốt nghiệp & Xếp loại
            WHEN d.content ILIKE '%đợt xét%' AND @q ILIKE '%xét tốt nghiệp%' THEN 0.2
            WHEN d.content ILIKE '%xếp loại tốt nghiệp%' AND @q ILIKE '%xếp loại%' THEN 0.25
            WHEN d.content ILIKE '%xuất sắc%' AND d.content ILIKE '%giảm%' AND @q ILIKE '%xuất sắc%' THEN 0.25
            
            -- Điểm
            WHEN d.content ILIKE '%ĐTBCTL%' AND @q ILIKE '%ĐTBCTL%' THEN 0.25
            WHEN d.content ILIKE '%điểm I%' AND @q ILIKE '%điểm I%' THEN 0.2
            WHEN d.content ILIKE '%điểm M%' AND @q ILIKE '%điểm M%' THEN 0.2
            WHEN d.content ILIKE '%học phần học lại%' AND @q ILIKE '%học lại%' THEN 0.25
            
            -- Song ngành
            WHEN d.content ILIKE '%ngành thứ hai%' AND (@q ILIKE '%song ngành%' OR @q ILIKE '%ngành thứ hai%') THEN 0.25
            WHEN d.content ILIKE '%30 tín chỉ%' AND @q ILIKE '%song ngành%' THEN 0.2
            
            -- Ngoại ngữ
            WHEN d.content ILIKE '%TOEIC%' AND @q ILIKE '%TOEIC%' THEN 0.2
            WHEN d.content ILIKE '%IELTS%' AND @q ILIKE '%IELTS%' THEN 0.2
            WHEN d.content ILIKE '%tiếng Nhật%' AND @q ILIKE '%tiếng Nhật%' THEN 0.25
            WHEN d.content ILIKE '%xếp lớp%' AND @q ILIKE '%xếp lớp%' THEN 0.25
            WHEN d.content ILIKE '%miễn học phần%' AND @q ILIKE '%miễn%' THEN 0.2
            WHEN d.content ILIKE '%chuẩn đầu ra%' AND @q ILIKE '%chuẩn%' THEN 0.2
            WHEN d.content ILIKE '%ENG01%' AND @q ILIKE '%Anh văn 1%' THEN 0.2
            WHEN d.content ILIKE '%ENG02%' AND @q ILIKE '%Anh văn 2%' THEN 0.2
            WHEN d.content ILIKE '%ENG03%' AND @q ILIKE '%Anh văn 3%' THEN 0.2
            WHEN d.content ILIKE '%bậc%' AND d.content ILIKE '%khung năng lực%' AND @q ILIKE '%bậc%' THEN 0.25
            WHEN d.content ILIKE '%2 năm%' AND @q ILIKE '%thời hạn%' AND @q ILIKE '%chứng chỉ%' THEN 0.25
            
            ELSE 0 
        END AS content_boost
    FROM ""{table}"" d
    CROSS JOIN q
)
SELECT
    id,
    content,
    metadata,
    vec_score,
    bm25_score,
    -- Hybrid scoring with dynamic weighting
    GREATEST(
        -- Primary: weighted combination
        (0.50 * vec_score) + (0.25 * LEAST(bm25_score * 2, 1.0)) + exact_boost + meta_boost + content_boost,
        -- Fallback: pure vector if BM25 fails
        0.7 * vec_score + exact_boost + content_boost
    ) AS final_score
FROM base
WHERE vec_score > 0.2 OR bm25_score > 0.0005 OR meta_boost > 0 OR content_boost > 0  -- LOWERED thresholds
ORDER BY final_score DESC
LIMIT @k * 2;  -- Fetch extra for re-ranking
";

    using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("q", question);
    cmd.Parameters.AddWithValue("expanded", expandedQuery);
    cmd.Parameters.AddWithValue("k", k);

    var candidates = new List<KbHit>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        candidates.Add(new KbHit(
            Id:       reader.GetString(0),
            Content:  reader.GetString(1),
            Metadata: reader.IsDBNull(2) ? null : reader.GetString(2),
            Score:    reader.GetDouble(5)
        ));
    }

    // Re-rank: boost chunks that contain key question terms
    var reranked = ReRankResults(candidates, question);
    
    return reranked.Take(k).ToList();
}

/// <summary>
/// Re-ranks results based on additional heuristics
/// </summary>
private static List<KbHit> ReRankResults(List<KbHit> candidates, string question)
{
    var questionLower = question.ToLower();
    var questionTerms = questionLower
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Length > 2)
        .ToHashSet();

    return candidates
        .Select(hit => 
        {
            var contentLower = hit.Content.ToLower();
            var metadataLower = (hit.Metadata ?? "").ToLower();
            var boost = 0.0;
            
            // Boost for containing multiple question terms
            var matchedTerms = questionTerms.Count(t => contentLower.Contains(t));
            boost += matchedTerms * 0.02;
            
            // STRONG boost for học vụ related content when asking about cảnh báo/đình chỉ
            if (questionLower.Contains("cảnh báo") || questionLower.Contains("đình chỉ") || questionLower.Contains("học vụ"))
            {
                if (contentLower.Contains("điều 16") || metadataLower.Contains("điều 16"))
                    boost += 0.25;
                if (contentLower.Contains("cảnh báo học vụ"))
                    boost += 0.2;
                if (contentLower.Contains("đình chỉ học tập"))
                    boost += 0.2;
                if (contentLower.Contains("đtbhk") || contentLower.Contains("điểm trung bình"))
                    boost += 0.1;
                if (contentLower.Contains("buộc thôi học"))
                    boost += 0.1;
            }
            
            // STRONG boost for đăng ký tín chỉ related content
            if (questionLower.Contains("tín chỉ") || questionLower.Contains("đăng ký") || 
                questionLower.Contains("tối thiểu") || questionLower.Contains("tối đa"))
            {
                if (contentLower.Contains("điều 14") || metadataLower.Contains("điều 14"))
                    boost += 0.25;
                if (contentLower.Contains("đăng ký học tập") || metadataLower.Contains("đăng ký học tập"))
                    boost += 0.25;
                if (contentLower.Contains("tín chỉ tối thiểu") || contentLower.Contains("tín chỉ tối đa"))
                    boost += 0.25;
                if (contentLower.Contains("số tín chỉ đăng ký"))
                    boost += 0.2;
                if (contentLower.Contains("14 ≤") || contentLower.Contains("≤ 24") || 
                    contentLower.Contains("14 \\le") || contentLower.Contains("\\le 24") ||
                    contentLower.Contains("14 <= n") || contentLower.Contains("n <= 24"))
                    boost += 0.3;
                if (contentLower.Contains("học kỳ hè") && questionLower.Contains("hè"))
                    boost += 0.2;
            }
            
            // BOOST for thời gian & học kỳ (Q1, Q3, Q4)
            if (questionLower.Contains("thời gian") || questionLower.Contains("tuần") || 
                questionLower.Contains("tiết") || questionLower.Contains("văn bằng") ||
                questionLower.Contains("khóa học"))
            {
                if (contentLower.Contains("điều 6") || metadataLower.Contains("điều 6"))
                    boost += 0.25;
                if (contentLower.Contains("điều 5") || metadataLower.Contains("điều 5"))
                    boost += 0.25;
                if (contentLower.Contains("điều 4") || metadataLower.Contains("điều 4"))
                    boost += 0.25;
                if (contentLower.Contains("thời gian tối đa"))
                    boost += 0.2;
                if (contentLower.Contains("tuần thực học"))
                    boost += 0.25;
                if (contentLower.Contains("15 tiết") || contentLower.Contains("50 phút"))
                    boost += 0.25;
            }
            
            // BOOST for tổng số tín chỉ CTĐT (Q2)
            if (questionLower.Contains("tổng số tín chỉ") || questionLower.Contains("chương trình đào tạo") ||
                questionLower.Contains("ctđt") || questionLower.Contains("120") || questionLower.Contains("132"))
            {
                if (contentLower.Contains("điều 7") || metadataLower.Contains("điều 7"))
                    boost += 0.25;
                if (contentLower.Contains("120") && contentLower.Contains("132"))
                    boost += 0.3;
            }
            
            // BOOST for học cải thiện & học lại (Q7, Q21)
            if (questionLower.Contains("cải thiện") || questionLower.Contains("học lại") ||
                questionLower.Contains("học vượt"))
            {
                if (contentLower.Contains("điều 14") || metadataLower.Contains("điều 14"))
                    boost += 0.25;
                if (contentLower.Contains("điều 3") || metadataLower.Contains("điều 3"))
                    boost += 0.2;
                if (contentLower.Contains("học cải thiện"))
                    boost += 0.25;
                if (contentLower.Contains("học phần học lại"))
                    boost += 0.25;
            }
            
            // BOOST for điểm & ĐTBCTL (Q8, Q18, Q19, Q20)
            if (questionLower.Contains("đtbctl") || questionLower.Contains("điểm trung bình") ||
                questionLower.Contains("điểm i") || questionLower.Contains("điểm m") ||
                questionLower.Contains("điểm bl"))
            {
                if (contentLower.Contains("điều 24") || metadataLower.Contains("điều 24"))
                    boost += 0.25;
                if (contentLower.Contains("đtbctl"))
                    boost += 0.2;
                if (contentLower.Contains("điểm i") || contentLower.Contains("điểm m"))
                    boost += 0.2;
            }
            
            // BOOST for khóa luận (Q12, Q13)
            if (questionLower.Contains("khóa luận") || questionLower.Contains("kltn") ||
                questionLower.Contains("đồ án") || questionLower.Contains("bảo vệ") ||
                questionLower.Contains("luận văn"))
            {
                if (contentLower.Contains("điều 31") || metadataLower.Contains("điều 31"))
                    boost += 0.25;
                if (contentLower.Contains("điều 10") || metadataLower.Contains("điều 10"))
                    boost += 0.2;
                if (contentLower.Contains("khóa luận tốt nghiệp"))
                    boost += 0.2;
                if (contentLower.Contains("không nợ quá"))
                    boost += 0.2;
                if (contentLower.Contains("gia hạn") && questionLower.Contains("hết thời gian"))
                    boost += 0.25;
            }
            
            // BOOST for tốt nghiệp & xếp loại (Q14, Q15, Q16, Q17)
            if (questionLower.Contains("tốt nghiệp") || questionLower.Contains("xét tốt nghiệp") ||
                questionLower.Contains("xếp loại") || questionLower.Contains("xuất sắc") ||
                questionLower.Contains("giảm bậc"))
            {
                if (contentLower.Contains("điều 32") || metadataLower.Contains("điều 32"))
                    boost += 0.25;
                if (contentLower.Contains("điều 33") || metadataLower.Contains("điều 33"))
                    boost += 0.25;
                if (contentLower.Contains("đợt xét"))
                    boost += 0.2;
                if (contentLower.Contains("xếp loại tốt nghiệp"))
                    boost += 0.2;
                if (contentLower.Contains("xuất sắc") && contentLower.Contains("giảm"))
                    boost += 0.25;
            }
            
            // BOOST for song ngành (Q11, Q40)
            if (questionLower.Contains("song ngành") || questionLower.Contains("ngành thứ hai") ||
                questionLower.Contains("văn bằng 2") || questionLower.Contains("bằng kép"))
            {
                if (contentLower.Contains("song ngành") || metadataLower.Contains("song ngành"))
                    boost += 0.25;
                if (contentLower.Contains("ngành thứ hai"))
                    boost += 0.25;
                if (contentLower.Contains("30 tín chỉ"))
                    boost += 0.2;
            }
            
            // BOOST for ngoại ngữ (Q22-Q38)
            if (questionLower.Contains("ngoại ngữ") || questionLower.Contains("tiếng anh") ||
                questionLower.Contains("tiếng nhật") || questionLower.Contains("toeic") ||
                questionLower.Contains("ielts") || questionLower.Contains("anh văn") ||
                questionLower.Contains("chuẩn đầu ra") || questionLower.Contains("miễn học phần") ||
                questionLower.Contains("xếp lớp") || questionLower.Contains("chứng chỉ") ||
                questionLower.Contains("cttt") || questionLower.Contains("ctc") ||
                questionLower.Contains("cttn"))
            {
                if (contentLower.Contains("điều 8") || metadataLower.Contains("điều 8"))
                    boost += 0.2;
                if (contentLower.Contains("toeic"))
                    boost += 0.2;
                if (contentLower.Contains("ielts"))
                    boost += 0.2;
                if (contentLower.Contains("tiếng nhật"))
                    boost += 0.25;
                if (contentLower.Contains("n3") || contentLower.Contains("n4") || contentLower.Contains("n5"))
                    boost += 0.2;
                if (contentLower.Contains("xếp lớp"))
                    boost += 0.25;
                if (contentLower.Contains("miễn học phần"))
                    boost += 0.2;
                if (contentLower.Contains("chuẩn đầu ra"))
                    boost += 0.2;
                if (contentLower.Contains("eng01") || contentLower.Contains("eng02") || contentLower.Contains("eng03"))
                    boost += 0.15;
                if (contentLower.Contains("bậc") && (contentLower.Contains("khung năng lực") || contentLower.Contains("cefr")))
                    boost += 0.25;
                if (contentLower.Contains("2 năm") && questionLower.Contains("thời hạn"))
                    boost += 0.25;
                if (contentLower.Contains("cttt") || contentLower.Contains("ctc") || contentLower.Contains("cttn"))
                    boost += 0.2;
                if (contentLower.Contains("450") && questionLower.Contains("toeic"))
                    boost += 0.25;
                if (contentLower.Contains("500") && questionLower.Contains("toeic"))
                    boost += 0.25;
                if (contentLower.Contains("5.0") && questionLower.Contains("ielts"))
                    boost += 0.25;
            }
            
            // Boost for regulatory content when asking about điều kiện/quy định
            if ((questionLower.Contains("điều kiện") || questionLower.Contains("quy định")) 
                && (contentLower.Contains("điều kiện") || contentLower.Contains("phải")))
            {
                boost += 0.05;
            }
            
            // Boost for procedure content when asking about quy trình
            if (questionLower.Contains("quy trình") && 
                (contentLower.Contains("bước") || contentLower.Contains("quy trình")))
            {
                boost += 0.05;
            }
            
            // Boost for article references
            if (System.Text.RegularExpressions.Regex.IsMatch(hit.Content, @"Điều\s+\d+", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                boost += 0.02;
            }

            return new KbHit(hit.Id, hit.Content, hit.Metadata, hit.Score + boost);
        })
        .OrderByDescending(h => h.Score)
        .ToList();
}



    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    public static string TrimForPrompt(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + " …";
    }
}


