using dotenv.net;
using LangChain.Providers;
using LangChain.Providers.Google;
using System.Text;

/// <summary>
/// Test runner để chạy các câu hỏi từ file test_quest.txt
/// </summary>
public class TestRunner
{
    public static async Task RunTestsAsync(string testFilePath, QAService qaService, string outputPath)
    {
        Console.OutputEncoding = Encoding.UTF8;
        
        if (!File.Exists(testFilePath))
        {
            Console.WriteLine($"❌ File không tồn tại: {testFilePath}");
            return;
        }

        var questions = File.ReadAllLines(testFilePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"📋 BẮT ĐẦU TEST VỚI {questions.Count} CÂU HỎI");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        var results = new List<TestResult>();
        var sb = new StringBuilder();
        sb.AppendLine("# KẾT QUẢ TEST RAG Q&A");
        sb.AppendLine($"Thời gian: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Tổng số câu hỏi: {questions.Count}");
        sb.AppendLine("\n---\n");

        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < questions.Count; i++)
        {
            var question = questions[i].Trim();
            Console.WriteLine($"\n[{i + 1}/{questions.Count}] 🔍 Câu hỏi: {question}");
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
                Console.WriteLine($"⚠️ Không tìm thấy context phù hợp | Time: {elapsed.TotalSeconds:F1}s");
            }

            // Hiển thị câu trả lời (rút gọn)
            var shortAnswer = qaResult.Answer.Length > 300 
                ? qaResult.Answer.Substring(0, 300) + "..." 
                : qaResult.Answer;
            Console.WriteLine($"\n📝 Trả lời:\n{shortAnswer}");

            // Ghi vào file output
            sb.AppendLine($"## Câu {i + 1}");
            sb.AppendLine($"**Câu hỏi:** {question}\n");
            sb.AppendLine($"**Hits:** {qaResult.HitCount} | **Top Score:** {qaResult.TopScore:F4} | **Time:** {elapsed.TotalMilliseconds}ms\n");
            sb.AppendLine($"**Trả lời:**\n{qaResult.Answer}\n");
            sb.AppendLine("---\n");

            // Delay để tránh rate limit
            if (i < questions.Count - 1)
            {
                await Task.Delay(1000);
            }
        }

        // Tổng kết
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("📊 TỔNG KẾT");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"✅ Thành công: {successCount}/{questions.Count} ({100.0 * successCount / questions.Count:F1}%)");
        Console.WriteLine($"⚠️ Không có context: {failCount}/{questions.Count}");
        Console.WriteLine($"📈 Điểm trung bình: {results.Where(r => r.TopScore > 0).Average(r => r.TopScore):F4}");
        Console.WriteLine($"⏱️ Thời gian trung bình: {results.Average(r => r.ElapsedMs):F0}ms");

        // Ghi summary vào file
        sb.AppendLine("\n# TỔNG KẾT\n");
        sb.AppendLine($"- Thành công: {successCount}/{questions.Count} ({100.0 * successCount / questions.Count:F1}%)");
        sb.AppendLine($"- Không có context: {failCount}/{questions.Count}");
        sb.AppendLine($"- Điểm trung bình: {results.Where(r => r.TopScore > 0).Average(r => r.TopScore):F4}");
        sb.AppendLine($"- Thời gian trung bình: {results.Average(r => r.ElapsedMs):F0}ms");

        // Lưu kết quả
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n💾 Kết quả đã được lưu vào: {outputPath}");
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
