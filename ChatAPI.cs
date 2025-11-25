using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// REST API wrapper for the QA chatbot service using ASP.NET Minimal API
/// </summary>
public class ChatApi
{
    private readonly QAService _qaService;

    public ChatApi(QAService qaService)
    {
        _qaService = qaService;
    }

    /// <summary>
    /// Start the API server on specified port
    /// </summary>
    public async Task StartAsync(int port = 5001)
    {
        var builder = WebApplication.CreateBuilder();

        // Configure JSON serialization
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // Configure CORS
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        // Register services
        builder.Services.AddSingleton(_qaService);

        var app = builder.Build();

        // Configure middleware
        app.UseCors();

        // ==================== ENDPOINTS ====================

        // Health check
        app.MapGet("/health", () => Results.Ok(new HealthResponse
        {
            Status = "healthy",
            Timestamp = DateTime.UtcNow,
            Version = "1.0.0"
        }))
        .WithName("HealthCheck");

        // POST /api/chat - Main chat endpoint
        app.MapPost("/api/chat", async (ChatApiRequest request, QAService qaService) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return Results.BadRequest(new ErrorResponse
                {
                    Error = "Question is required",
                    Code = "INVALID_REQUEST"
                });
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await qaService.AnswerQuestionAsync(
                    request.Question,
                    request.IncludeContext ?? false
                );

                stopwatch.Stop();

                return Results.Ok(new ChatResponse
                {
                    UserId = request.UserId,
                    Question = request.Question,
                    Answer = result.Answer,
                    Context = request.IncludeContext == true ? result.Context : null,
                    HasContext = result.HasContext,
                    HitCount = result.HitCount,
                    TopScore = result.TopScore,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return Results.Json(new ErrorResponse
                {
                    Error = "Failed to process question",
                    Code = "PROCESSING_ERROR",
                    Details = ex.Message
                }, statusCode: 500);
            }
        })
        .WithName("Chat");

        // Print startup info
        Console.WriteLine();

        Console.WriteLine("Endpoints:");
        Console.WriteLine("POST /api/chat    - Ask a question   ");
        Console.WriteLine("GET  /health      - Health check     ");
        Console.WriteLine();

        await app.RunAsync($"http://localhost:{port}");
    }
}

// ==================== REQUEST DTOs ====================
public record ChatApiRequest
{
    public string? UserId { get; init; }
    public string Question { get; init; } = string.Empty;
    public bool? IncludeContext { get; init; }
}

// ==================== RESPONSE DTOs ====================
public record HealthResponse
{
    public string Status { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Version { get; init; } = string.Empty;
}

public record ChatResponse
{
    public string? UserId { get; init; }
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string? Context { get; init; }
    public bool HasContext { get; init; }
    public int HitCount { get; init; }
    public double TopScore { get; init; }
    public long ProcessingTimeMs { get; init; }
}

public record ErrorResponse
{
    public string Error { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Details { get; init; }
}