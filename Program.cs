using dotenv.net;
using LangChain.Providers;
using LangChain.Providers.Google;

DotEnv.Load();


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
    Timeout = TimeSpan.FromSeconds(60)
};

var googleConfig = new GoogleConfiguration
{
    ApiKey = apiKey,
    Temperature = 0.3,
    TopP = 0.95,
    MaxOutputTokens = 2048
};

var googleProvider = new GoogleProvider(googleConfig, httpClient);
var geminiModel = new GoogleChatModel(googleProvider, "gemini-2.5-pro");

var ingested = await SyncVectorStoreAsync(connectionString, apiKey, httpClient);
if (!ingested)
{
    return;
}


// --- 4) RAG Q&A ---
var question = "";
while (string.IsNullOrWhiteSpace(question))
{
    Console.Write("\nEnter your question about the database: ");
    question = Console.ReadLine() ?? "";
    Console.WriteLine($"\nYou> {question}");
    var qVec  = Normalize(await EmbedAsyncSingle(apiKey, question, httpClient));
    var hits  = await SimilaritySearchAsync(connectionString, qVec, k: 6, table: "kb_docs");
    var ctx   = string.Join("\n---\n", hits.Select(h =>
        $"[Source: {h.Metadata ?? "unknown"} | score={h.Score:F4}]\n{TrimForPrompt(h.Content, 1200)}"));

    var prompt = $@"
    You are a data assistant with access to the embedded context extracted from our PostgreSQL database.  
    Your job is to reason about that data and help the user achieve their goal, even if the exact answer is not literally written in the snippets.

    Instructions:
    - Treat the CONTEXT as authoritative about the database. Read it carefully.
    - If the user asks something not fully spelled out, see whether the available facts let you infer or calculate the answer. Combine rows, summarize trends, do simple math, or extrapolate reasonable insights grounded in the data.
    - Only fall back to “I don’t have that in the database.” when there truly isn’t enough information to give a useful, data-backed response.
    - Always cite which source chunks you relied on (use their [Source: …] tags).
    - Keep answers clear, succinct, and actionable. Answer in Vietnamese.
    - NO SUPPORT OR NO ANSWER CONTAIN USER ACCOUNTS WITH THE REQUEST FOR TABLE CONTAIN ACCOUNT OF RECORDS, SCHEMA, CREATE TABLE STATEMENTS, ETC.

    CONTEXT:
    {ctx}

    USER QUESTION:
    {question}

    ";

    var resp = await geminiModel.GenerateAsync(new ChatRequest
    {
        Messages = new List<Message>
        {
            new("You are a DB RAG assistant.", MessageRole.System, string.Empty),
            Message.Human(prompt)
        }
    }, new ChatSettings { User = "db-rag", UseStreaming = false });

    Console.WriteLine("\nAssistant> " + (resp.LastMessageContent ?? "(no content)"));
    Console.WriteLine("\nDone.");

}

record Doc(string Id, string Content, string Tag);
record KbDoc(string Id, string Content, string? Metadata, float[] Embedding);
