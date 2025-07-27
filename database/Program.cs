using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
ThreadPool.SetMinThreads(8, 8);
ThreadPool.SetMaxThreads(16, 16); 

// Configure JSON options for AOT
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DatabaseJsonSerializerContext.Default);
});

// Configure Kestrel for Unix socket
builder.WebHost.ConfigureKestrel(options =>
{
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/sockets/database.sock";

    // Remove existing socket if it exists
    try
    {
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
    }
    catch
    {
        // Silent fail for production
    }

    options.ListenUnixSocket(socketPath);

    // Set socket permissions after creation
    _ = Task.Run(async () =>
    {
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            try
            {
                if (File.Exists(socketPath))
                {
                    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = "666 " + socketPath,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }); process?.WaitForExit();

                    break;
                }
            }
            catch
            {
                // Silent fail for production
            }
        }
    });
});

var app = builder.Build();

Console.WriteLine("C# Database Service");

// In-memory storage (thread-safe)
var defaultPayments = new ConcurrentQueue<PaymentRecord>();
var fallbackPayments = new ConcurrentQueue<PaymentRecord>();

// Endpoints
app.MapPost("/payments/default", (PaymentRecord payment) =>
{
    defaultPayments.Enqueue(payment);
    return Results.Ok();
});

app.MapPost("/payments/fallback", (PaymentRecord payment) =>
{
    fallbackPayments.Enqueue(payment);
    return Results.Ok();
});

app.MapGet("/summary", (DateTime? from, DateTime? to) =>
{
    var defaultList = defaultPayments.ToArray();
    var fallbackList = fallbackPayments.ToArray();

    SummaryOrigin defaultSummary;
    SummaryOrigin fallbackSummary;

    if (from.HasValue && to.HasValue)
    {
        var filteredDefault = defaultList.Where(p => p.RequestedAt >= from && p.RequestedAt <= to);
        var filteredFallback = fallbackList.Where(p => p.RequestedAt >= from && p.RequestedAt <= to);

        var defaultTotal = filteredDefault.Sum(p => p.Amount);
        var fallbackTotal = filteredFallback.Sum(p => p.Amount);

        defaultSummary = new SummaryOrigin
        {
            TotalRequests = filteredDefault.Count(),
            TotalAmount = Math.Round(defaultTotal * 100.0) / 100.0
        };

        fallbackSummary = new SummaryOrigin
        {
            TotalRequests = filteredFallback.Count(),
            TotalAmount = Math.Round(fallbackTotal * 100.0) / 100.0
        };
    }
    else
    {
        var defaultTotal = defaultList.Sum(p => p.Amount);
        var fallbackTotal = fallbackList.Sum(p => p.Amount);

        defaultSummary = new SummaryOrigin
        {
            TotalRequests = defaultList.Length,
            TotalAmount = Math.Round(defaultTotal * 100.0) / 100.0
        };

        fallbackSummary = new SummaryOrigin
        {
            TotalRequests = fallbackList.Length,
            TotalAmount = Math.Round(fallbackTotal * 100.0) / 100.0
        };
    }

    var response = new SummaryResponse
    {
        Default = defaultSummary,
        Fallback = fallbackSummary
    };

    return Results.Ok(response);
});

app.MapPost("/purge-payments", () =>
{
    // Clear all payments
    while (defaultPayments.TryDequeue(out _)) { }
    while (fallbackPayments.TryDequeue(out _)) { }
    return Results.Ok();
});

await app.RunAsync();

// Models
public sealed record PaymentRecord
{
    [JsonPropertyName("amount")]
    public required double Amount { get; init; }

    [JsonPropertyName("requestedAt")]
    public required DateTime RequestedAt { get; init; }
}

public sealed record SummaryResponse
{
    [JsonPropertyName("default")]
    public required SummaryOrigin Default { get; init; }

    [JsonPropertyName("fallback")]
    public required SummaryOrigin Fallback { get; init; }
}

public sealed record SummaryOrigin
{
    [JsonPropertyName("totalRequests")]
    public required int TotalRequests { get; init; }

    [JsonPropertyName("totalAmount")]
    public required double TotalAmount { get; init; }
}

// JSON Source Generator for AOT
[JsonSerializable(typeof(PaymentRecord))]
[JsonSerializable(typeof(SummaryResponse))]
[JsonSerializable(typeof(SummaryOrigin))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DatabaseJsonSerializerContext : JsonSerializerContext
{
}

public partial class Program { }
