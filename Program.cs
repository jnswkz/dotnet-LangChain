using dotenv.net;
using LangChain.Providers;
using LangChain.Providers.Google;
using System.IO;
using System.Text;
using Npgsql;

DotEnv.Load();

Console.InputEncoding  = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

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

// --- CHECK FOR TEST MODE ---
// Check both .env and environment variable
var runTest = (env.TryGetValue("RUN_TEST", out var testFlag) && string.Equals(testFlag, "1", StringComparison.OrdinalIgnoreCase))
              || string.Equals(Environment.GetEnvironmentVariable("RUN_TEST"), "1", StringComparison.OrdinalIgnoreCase);

var runDebug = (env.TryGetValue("DEBUG_SEARCH", out var debugFlag) && string.Equals(debugFlag, "1", StringComparison.OrdinalIgnoreCase))
              || string.Equals(Environment.GetEnvironmentVariable("DEBUG_SEARCH"), "1", StringComparison.OrdinalIgnoreCase);

if (runDebug)
{
    await DebugSearch.RunDebugAsync();
    return;
}

if (runTest)
{
    Console.WriteLine("\n🧪 RUNNING TEST MODE...\n");
    var testQaService = new QAService(connectionString, apiKey, httpClient, geminiModel);
    
    var testFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "test_quest.txt");
    if (!File.Exists(testFilePath))
    {
        testFilePath = "test_quest.txt"; // fallback to current directory
    }
    
    var outputPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "test_results.md");
    
    await TestRunner.RunTestsAsync(testFilePath, testQaService, outputPath);
    return;
}

// --- CHECK FOR API MODE ---
var runApi = (env.TryGetValue("RUN_API", out var apiFlag) && string.Equals(apiFlag, "1", StringComparison.OrdinalIgnoreCase))
             || string.Equals(Environment.GetEnvironmentVariable("RUN_API"), "1", StringComparison.OrdinalIgnoreCase);

var apiPort = 5001;
if (env.TryGetValue("API_PORT", out var portStr) && int.TryParse(portStr, out var parsedPort))
{
    apiPort = parsedPort;
}

var qaService = new QAService(connectionString, apiKey, httpClient, geminiModel);

if (runApi)
{
    var chatApi = new ChatApi(qaService);
    await chatApi.StartAsync(apiPort);
    return;
}

// --- INTERACTIVE MODE ---
Console.WriteLine("\n💬 Interactive Chat Mode");
Console.WriteLine("Type your question, 'api' to start API server, or 'exit' to quit\n");

while (true)
{
    Console.Write("You> ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Equals("api", StringComparison.OrdinalIgnoreCase))
    {
        var chatApi = new ChatApi(qaService);
        await chatApi.StartAsync(apiPort);
        break;
    }

    try
    {
        var result = await qaService.AnswerQuestionAsync(input, showContext: true);
        
        if (result.HasContext && result.Context != null)
        {
            Console.WriteLine("\n====== RAG CONTEXT ======");
            Console.WriteLine(result.Context);
        }
        
        Console.WriteLine("\n====== ANSWER ======");
        Console.WriteLine(result.Answer);
        Console.WriteLine($"\n⏱️ Hits: {result.HitCount} | Top Score: {result.TopScore:F4}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ Error: {ex.Message}\n");
    }
}
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
