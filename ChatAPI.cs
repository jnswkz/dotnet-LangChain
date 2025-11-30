using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
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

        // Configure Swagger/OpenAPI
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "eUIT Chatbot API",
                Version = "v1",
                Description = "REST API for the eUIT QA Chatbot service powered by Gemini AI",
                Contact = new OpenApiContact
                {
                    Name = "eUIT Team",
                    Email = "support@euit.edu.vn"
                }
            });
        });

        // Register services
        builder.Services.AddSingleton(_qaService);

        var app = builder.Build();

        // Configure middleware
        app.UseCors();

        // Enable Swagger UI
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "eUIT Chatbot API v1");
            options.RoutePrefix = string.Empty; // Swagger UI at root
            options.DocumentTitle = "eUIT Chatbot API";
        });

        // ==================== ENDPOINTS ====================

        // Health check
        app.MapGet("/health", () => Results.Ok(new HealthResponse
        {
            Status = "healthy",
            Timestamp = DateTime.UtcNow,
            Version = "1.0.0"
        }))
        .WithName("HealthCheck")
        .WithTags("System")
        .WithSummary("Health Check")
        .WithDescription("Returns the health status of the API service")
        .Produces<HealthResponse>(StatusCodes.Status200OK);

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
        .WithName("Chat")
        .WithTags("Chat")
        .WithSummary("Ask a Question")
        .WithDescription("Send a question to the AI chatbot and receive an answer based on the knowledge base")
        .Produces<ChatResponse>(StatusCodes.Status200OK)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError);

        // Print startup info
        Console.WriteLine();

        Console.WriteLine("Endpoints:");
        Console.WriteLine("POST /api/chat    - Ask a question   ");
        Console.WriteLine("GET  /health      - Health check     ");
        Console.WriteLine();
        Console.WriteLine($"Swagger UI: http://localhost:{port}/");
        Console.WriteLine($"Swagger JSON: http://localhost:{port}/swagger/v1/swagger.json");
        Console.WriteLine();

        await app.RunAsync($"http://0.0.0.0:{port}");
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