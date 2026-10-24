using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Text.Json.Serialization;
using WebApplicationAI.AI.Abstractions;
using WebApplicationAI.Data;
using WebApplicationAI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var ollama = new OpenAIClient(
    new ApiKeyCredential("ollama"),
    new OpenAIClientOptions
    {
        Endpoint = new Uri("http://localhost:11434/v1"),
        // Local CPU inference is slow; the SDK default is 100s.
        NetworkTimeout = TimeSpan.FromMinutes(10)
    });

builder.Services.AddChatClient(ollama.GetChatClient(builder.Configuration["Ollama:ChatModel"] ?? "llama3.2").AsIChatClient());
builder.Services.AddEmbeddingGenerator(ollama.GetEmbeddingClient(builder.Configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text").AsIEmbeddingGenerator());

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("ResumeDb"),
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.Schema)));

builder.Services.AddScoped<IAiClientService, AiClientService>();
builder.Services.AddSingleton<IResumeTextExtractor, ResumeTextExtractor>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();
builder.Services.AddHttpClient<IJobFeedService, JobFeedService>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<RagService>();
builder.Services.AddScoped<JobStore>();

var app = builder.Build();

// Create/upgrade the resumeai schema. Logged rather than fatal so the DB-free endpoints still run.
using (var scope = app.Services.CreateScope())
{
    try
    {
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database migration failed; database endpoints will not work.");
    }
}

// Ollama / database failures become clean 502/504s instead of stack traces.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    (int status, string error) = ex switch
    {
        // EF wraps connection failures in InvalidOperationException, so check the database first.
        Npgsql.NpgsqlException or DbUpdateException or { InnerException: Npgsql.NpgsqlException } => (503, "The database is unavailable."),
        OperationCanceledException when !context.RequestAborted.IsCancellationRequested => (504, "The AI model or a job feed timed out."),
        ClientResultException or HttpRequestException or InvalidOperationException => (502, "The AI model is unavailable or returned an invalid response."),
        _ => (500, "Unexpected error.")
    };
    app.Logger.LogError(ex, "Request failed.");
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error });
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.MapGet("/", () => "AI API is running with Ollama!");

// Health: opens a DB connection and queries every table, so it proves the schema exists, not just the port.
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        var tables = new
        {
            resumes = await db.Resumes.CountAsync(ct),
            jobs = await db.Jobs.CountAsync(ct),
            openJobs = await db.Jobs.CountAsync(j => j.IsOpen, ct),
            chunks = await db.Chunks.CountAsync(ct),
            applications = await db.Applications.CountAsync(ct)
        };
        var conn = db.Database.GetDbConnection();
        return Results.Ok(new { status = "Healthy", database = new { conn.DataSource, conn.Database, schema = AppDbContext.Schema, tables } });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Health check failed.");
        return Results.Json(new { status = "Unhealthy", database = (ex.InnerException ?? ex).Message }, statusCode: 503);
    }
});

app.Run();
