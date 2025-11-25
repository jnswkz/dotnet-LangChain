// Debug script to search for specific content in kb_docs
// Run with: dotnet run -- debug

using dotenv.net;
using Npgsql;
using System.Text;

public class DebugSearch
{
    public static async Task RunDebugAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        DotEnv.Load();
        var env = DotEnv.Read();
        
        if (!env.TryGetValue("AZURE_POSTGRES_URL", out var cs))
        {
            Console.WriteLine("AZURE_POSTGRES_URL missing");
            return;
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        Console.WriteLine("\n=== SEARCHING FOR: số tín chỉ đăng ký học kỳ ===\n");

        // Search 1: Find documents with "tín chỉ" and "đăng ký"
        var sql1 = @"
SELECT id, metadata, LEFT(content, 600) as preview
FROM kb_docs 
WHERE content ILIKE '%tín chỉ%đăng ký%'
   OR content ILIKE '%đăng ký%tín chỉ%'
ORDER BY id
LIMIT 15;
";
        Console.WriteLine("=== Query 1: tín chỉ + đăng ký ===");
        await RunQueryAsync(conn, sql1);

        // Search 2: Find documents specifically about minimum/maximum credits
        var sql2 = @"
SELECT id, metadata, LEFT(content, 600) as preview
FROM kb_docs 
WHERE (content ILIKE '%14 tín chỉ%' OR content ILIKE '%24 tín chỉ%' 
       OR content ILIKE '%25 tín chỉ%' OR content ILIKE '%12 tín chỉ%')
ORDER BY id
LIMIT 10;
";
        Console.WriteLine("\n=== Query 2: Specific credit numbers (14, 24, 25, 12) ===");
        await RunQueryAsync(conn, sql2);

        // Search 3: Look for "Điều 9" or similar regulation about registration
        var sql3 = @"
SELECT id, metadata, LEFT(content, 600) as preview
FROM kb_docs 
WHERE content ILIKE '%điều 9%' 
   OR content ILIKE '%điều 10%'
   OR content ILIKE '%đăng ký học phần%'
ORDER BY id
LIMIT 10;
";
        Console.WriteLine("\n=== Query 3: Điều 9/10 or đăng ký học phần ===");
        await RunQueryAsync(conn, sql3);

        // Search 4: Full text search for registration limits
        var sql4 = @"
SELECT id, metadata, LEFT(content, 600) as preview,
       ts_rank(tsv, plainto_tsquery('simple', 'tín chỉ đăng ký học kỳ')) as rank
FROM kb_docs 
WHERE tsv @@ plainto_tsquery('simple', 'tín chỉ đăng ký')
ORDER BY rank DESC
LIMIT 10;
";
        Console.WriteLine("\n=== Query 4: Full-text search 'tín chỉ đăng ký' ===");
        await RunQueryAsync(conn, sql4);

        // Search 5: Look for semester registration content
        var sql5 = @"
SELECT id, metadata, LEFT(content, 800) as preview
FROM kb_docs 
WHERE content ILIKE '%khối lượng học tập%'
   OR content ILIKE '%số tín chỉ%học kỳ%'
   OR content ILIKE '%giới hạn%tín chỉ%'
ORDER BY id
LIMIT 10;
";
        Console.WriteLine("\n=== Query 5: khối lượng học tập / giới hạn tín chỉ ===");
        await RunQueryAsync(conn, sql5);
    }

    private static async Task RunQueryAsync(NpgsqlConnection conn, string sql)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            
            int count = 0;
            while (await reader.ReadAsync())
            {
                count++;
                Console.WriteLine($"\n--- Result {count} ---");
                Console.WriteLine($"ID: {reader.GetString(0)}");
                Console.WriteLine($"Metadata: {(reader.IsDBNull(1) ? "null" : reader.GetString(1))}");
                Console.WriteLine($"Preview: {reader.GetString(2)}");
            }
            
            if (count == 0)
            {
                Console.WriteLine("No results found.");
            }
            else
            {
                Console.WriteLine($"\nTotal: {count} results");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
