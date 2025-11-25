using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

partial class Program
{
    protected static async Task<float[]> EmbedAsyncSingle(string apiKey, string text, HttpClient http, bool isQuery = false)
    {
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={apiKey}";
        var body = new
        {
            model = "models/text-embedding-004",
            content = new { parts = new[] { new { text } } },
            // Use RETRIEVAL_QUERY for queries, RETRIEVAL_DOCUMENT for documents
            taskType = isQuery ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT"
        };

        using var resp = await http.PostAsJsonAsync(url, body, jsonOpts);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var values = doc.RootElement.GetProperty("embedding").GetProperty("values");
        var vec = new float[values.GetArrayLength()];
        for (int i = 0; i < vec.Length; i++) vec[i] = values[i].GetSingle();
        return vec;
    }

    protected static async Task<float[][]> EmbedAsyncBatch(string apiKey, IEnumerable<string> texts, HttpClient http)
    {
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var list = texts.ToList();
        if (list.Count == 0) return Array.Empty<float[]>();

        const int maxBatchSize = 20;
        const int maxRetries = 3;
        const int baseDelayMs = 500;
        var result = new List<float[]>(capacity: list.Count);
        for (int offset = 0; offset < list.Count; offset += maxBatchSize)
        {
            var batch = list.Skip(offset).Take(maxBatchSize).ToList();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:batchEmbedContents?key={apiKey}";
                    var requests = batch.Select(t => new
                    {
                        model = "models/text-embedding-004",
                        content = new { parts = new[] { new { text = t } } },
                        taskType = "RETRIEVAL_DOCUMENT"
                    });
                    var body = new { requests };

                    using var resp = await http.PostAsJsonAsync(url, body, jsonOpts);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync();
                        throw new Exception($"Embedding batch failed: {resp.StatusCode} {err}");
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    var arr = doc.RootElement.GetProperty("embeddings");
                    for (int i = 0; i < arr.GetArrayLength(); i++)
                    {
                        var vals = arr[i].GetProperty("values");
                        var vec = new float[vals.GetArrayLength()];
                        for (int j = 0; j < vec.Length; j++) vec[j] = vals[j].GetSingle();
                        result.Add(vec);
                    }
                    break; // succeeded
                }
                catch (HttpRequestException ex) when (attempt < maxRetries - 1)
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay);
                    continue;
                }
                catch (TaskCanceledException) when (attempt < maxRetries - 1)
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay);
                    continue;
                }
            }
        }
        return result.ToArray();
    }

    protected static float[] Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
        {
            sum += (double)v[i] * v[i];
        }

        var norm = (float)Math.Sqrt(sum);
        if (norm == 0) return v;

        var outv = new float[v.Length];
        for (int i = 0; i < v.Length; i++) outv[i] = v[i] / norm;
        return outv;
    }
}
