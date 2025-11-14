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
                content = content.Substring(0, maxDocChars) + "\n�?�";

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
  content TEXT NOT NULL,
  metadata TEXT NULL,
  embedding vector({dim}) NOT NULL
);";
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
        sb.Append($@"INSERT INTO ""{table}"" (id, content, metadata, embedding) VALUES ");

        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append($"(@id{i}, @content{i}, @meta{i}, @emb{i}::vector)");
        }
        sb.Append(@" ON CONFLICT (id) DO UPDATE SET 
        content = EXCLUDED.content,
        metadata = EXCLUDED.metadata,
        embedding = EXCLUDED.embedding;");

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

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string TrimForPrompt(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + " …";
    }
}


