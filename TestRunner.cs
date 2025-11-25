using dotenv.net;
using LangChain.Providers;
using LangChain.Providers.Google;
using System.Text;

/// <summary>
/// Test runner to execute questions from test_quest.txt file
/// </summary>
public class TestRunner
{
    public static async Task RunTestsAsync(string testFilePath, QAService qaService, string outputPath)
    {
        Console.OutputEncoding = Encoding.UTF8;
        
        if (!File.Exists(testFilePath))
        {
            Console.WriteLine($"❌ File not found: {testFilePath}");
            return;
        }

        var questions = File.ReadAllLines(testFilePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"📋 STARTING TEST WITH {questions.Count} QUESTIONS");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        var results = new List<TestResult>();
        var sb = new StringBuilder();
        sb.AppendLine("# RAG Q&A TEST RESULTS");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total questions: {questions.Count}");
        sb.AppendLine("\n---\n");

        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < questions.Count; i++)
        {
            var question = questions[i].Trim();
            Console.WriteLine($"\n[{i + 1}/{questions.Count}] 🔍 Question: {question}");
            Console.WriteLine(new string('-', 60));

            var startTime = DateTime.Now;
            var qaResult = await qaService.AnswerQuestionAsync(question, showContext: false);
            var elapsed = DateTime.Now - startTime;

            var testResult = new TestResult
            {
                Index = i + 1,
                Question = question,
                Answer = qaResult.Answer,
                HitCount = qaResult.HitCount,
                TopScore = qaResult.TopScore,
                HasContext = qaResult.HasContext,
                ElapsedMs = (int)elapsed.TotalMilliseconds,
                Success = string.IsNullOrEmpty(qaResult.Error) && qaResult.HitCount > 0
            };
            results.Add(testResult);

            // Hiển thị kết quả
            if (testResult.Success)
            {
                successCount++;
                Console.WriteLine($"✅ Hits: {qaResult.HitCount} | Top Score: {qaResult.TopScore:F4} | Time: {elapsed.TotalSeconds:F1}s");
            }
            else
            {
                failCount++;
                Console.WriteLine($"⚠️ No matching context found | Time: {elapsed.TotalSeconds:F1}s");
            }

            // Hiển thị câu trả lời (rút gọn)
            var shortAnswer = qaResult.Answer.Length > 300 
                ? qaResult.Answer.Substring(0, 300) + "..." 
                : qaResult.Answer;
            Console.WriteLine($"\n📝 Answer:\n{shortAnswer}");

            // Ghi vào file output
            sb.AppendLine($"## Question {i + 1}");
            sb.AppendLine($"**Question:** {question}\n");
            sb.AppendLine($"**Hits:** {qaResult.HitCount} | **Top Score:** {qaResult.TopScore:F4} | **Time:** {elapsed.TotalMilliseconds}ms\n");
            sb.AppendLine($"**Answer:**\n{qaResult.Answer}\n");
            sb.AppendLine("---\n");

            // Delay để tránh rate limit
            if (i < questions.Count - 1)
            {
                await Task.Delay(1000);
            }
        }

        // Summary
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("📊 SUMMARY");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"✅ Success: {successCount}/{questions.Count} ({100.0 * successCount / questions.Count:F1}%)");
        Console.WriteLine($"⚠️ No context: {failCount}/{questions.Count}");
        Console.WriteLine($"📈 Average score: {results.Where(r => r.TopScore > 0).Average(r => r.TopScore):F4}");
        Console.WriteLine($"⏱️ Average time: {results.Average(r => r.ElapsedMs):F0}ms");

        // Write summary to file
        sb.AppendLine("\n# SUMMARY\n");
        sb.AppendLine($"- Success: {successCount}/{questions.Count} ({100.0 * successCount / questions.Count:F1}%)");
        sb.AppendLine($"- No context: {failCount}/{questions.Count}");
        sb.AppendLine($"- Average score: {results.Where(r => r.TopScore > 0).Average(r => r.TopScore):F4}");
        sb.AppendLine($"- Average time: {results.Average(r => r.ElapsedMs):F0}ms");

        // Save results
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n💾 Results saved to: {outputPath}");
    }
}

public class TestResult
{
    public int Index { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public int HitCount { get; set; }
    public double TopScore { get; set; }
    public bool HasContext { get; set; }
    public int ElapsedMs { get; set; }
    public bool Success { get; set; }
}
