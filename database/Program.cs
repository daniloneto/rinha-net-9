using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Concurrent;
using LockFree.EventStore;

var builder = WebApplication.CreateBuilder(args);

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DatabaseJsonSerializerContext.Default);
});

builder.WebHost.ConfigureKestrel(options =>
{
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/sockets/database.sock";
    try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }

    options.ListenUnixSocket(socketPath);

    // Otimizações conservadoras de performance para database
    options.Limits.MaxConcurrentConnections = 500;
    options.Limits.MaxRequestBodySize = 512; // 512B - payloads muito pequenos
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                try
                {
                    if (File.Exists(socketPath))
                    {
                        File.SetUnixFileMode(
                            socketPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite |
                            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                        );
                        break;
                    }
                }
                catch { /* Ignorar falhas */ }
            }
        });
    }
});

var app = builder.Build();
Console.WriteLine("C# Database Service");

var maxCapacity = 100_000;

var paymentService = new PaymentService(maxCapacity);

app.MapPost("/payments/default", (PaymentRecord payment) => ProcessPayment(payment, isFallback: false));
app.MapPost("/payments/fallback", (PaymentRecord payment) => ProcessPayment(payment, isFallback: true));

IResult ProcessPayment(PaymentRecord payment, bool isFallback)
{
    return paymentService.AddPayment(payment, isFallback)
        ? Results.Created()
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}

app.MapGet("/summary", (DateTime? from, DateTime? to) =>
{
    // Se não vier filtro, retorna 200 vazio
    if (!from.HasValue || !to.HasValue)
    {
        return Results.Ok();
    }    var (defaultSummary, fallbackSummary) = paymentService.GetSummary(from, to);    var response = new DatabaseSummaryResponse
    {
        Default = defaultSummary,
        Fallback = fallbackSummary
    };

    return Results.Ok(response);
});

app.MapGet("/metrics", () =>
{
    var metrics = paymentService.GetMetrics();
    return Results.Ok(metrics);
});

app.MapPost("/purge-payments", () =>
{
    paymentService.PurgePayments();
    return Results.Ok();
});

await app.RunAsync();

public sealed record PaymentRecord
{
    [JsonPropertyName("a")]
    public required double Amount { get; init; }

    [JsonPropertyName("t")]
    public required DateTime RequestedAt { get; init; }
}

public sealed record DatabaseSummaryResponse
{
    [JsonPropertyName("d")]
    public required DatabaseSummaryOrigin Default { get; init; }

    [JsonPropertyName("f")]
    public required DatabaseSummaryOrigin Fallback { get; init; }
}

public sealed record DatabaseSummaryOrigin
{
    [JsonPropertyName("c")]
    public required int TotalRequests { get; init; }

    [JsonPropertyName("s")]
    public required double TotalAmount { get; init; }
}

public sealed record MetricsResponse
{
    [JsonPropertyName("defaultStore")]
    public required StoreMetrics DefaultStore { get; init; }

    [JsonPropertyName("fallbackStore")]
    public required StoreMetrics FallbackStore { get; init; }
}

public sealed record StoreMetrics
{
    [JsonPropertyName("count")]
    public required long Count { get; init; }

    [JsonPropertyName("capacity")]
    public required int Capacity { get; init; }

    [JsonPropertyName("isEmpty")]
    public required bool IsEmpty { get; init; }

    [JsonPropertyName("isFull")]
    public required bool IsFull { get; init; }

    [JsonPropertyName("discardedEvents")]
    public required long DiscardedEvents { get; init; }

    [JsonPropertyName("totalAppended")]
    public required long TotalAppended { get; init; }

    [JsonPropertyName("appendsPerSecond")]
    public required double AppendsPerSecond { get; init; }

    [JsonPropertyName("lastAppendTime")]
    public required DateTime? LastAppendTime { get; init; }
}

public sealed class PaymentService
{
    private EventStore<PaymentEvent> _defaultStore;
    private EventStore<PaymentEvent> _fallbackStore;
    private readonly int _maxCapacity;

    public PaymentService(int maxCapacity)
    {
        _maxCapacity = maxCapacity;        var options = new EventStoreOptions<PaymentEvent>
        {
            Capacity = maxCapacity,
            Partitions = 8,
            TimestampSelector = new PaymentEventTimestampSelector(),
            EnableFalseSharingProtection = true
        };

        _defaultStore = new EventStore<PaymentEvent>(options);
        _fallbackStore = new EventStore<PaymentEvent>(options);
    }

    public bool AddPayment(PaymentRecord payment, bool isFallback)
    {

        var paymentEvent = new PaymentEvent
        {
            Amount = payment.Amount,
            RequestedAt = payment.RequestedAt.Kind == DateTimeKind.Utc
                ? payment.RequestedAt
                : payment.RequestedAt.ToUniversalTime()
        };

        var store = isFallback ? _fallbackStore : _defaultStore;

        // A EventStore tem descarte FIFO automático, então sempre retornamos true
        // se conseguirmos adicionar o evento
        return store.TryAppend(paymentEvent);
    }    public (DatabaseSummaryOrigin Default, DatabaseSummaryOrigin Fallback) GetSummary(DateTime? from, DateTime? to)
    {
        var fromUtc = from!.Value.ToUniversalTime();
        var toUtc = to!.Value.ToUniversalTime();

        var defaultSummary = CalculateSummary(_defaultStore, fromUtc, toUtc);
        var fallbackSummary = CalculateSummary(_fallbackStore, fromUtc, toUtc);

        return (defaultSummary, fallbackSummary);
    }
    private DatabaseSummaryOrigin CalculateSummary(EventStore<PaymentEvent> store, DateTime from, DateTime to)
    {
        var totalRequests = store.CountEvents(from, to);
        var totalAmount = store.Sum(evt => evt.Amount, from, to);

        return new DatabaseSummaryOrigin
        {
            TotalRequests = (int)totalRequests,
            TotalAmount = Math.Round(totalAmount, 2)
        };
    }
    public void PurgePayments()
    {
        // Usando o novo método Clear() da v0.1.2!
        _defaultStore.Clear();
        _fallbackStore.Clear();
    }    public MetricsResponse GetMetrics()
    {
        var defaultStats = _defaultStore.Statistics;
        var fallbackStats = _fallbackStore.Statistics;

        return new MetricsResponse
        {
            DefaultStore = new StoreMetrics
            {
                Count = _defaultStore.Count,
                Capacity = _defaultStore.Capacity,
                IsEmpty = _defaultStore.IsEmpty,
                IsFull = _defaultStore.IsFull,
                DiscardedEvents = defaultStats.TotalDiscarded,
                TotalAppended = defaultStats.TotalAppended,
                AppendsPerSecond = defaultStats.AppendsPerSecond,
                LastAppendTime = defaultStats.LastAppendTime
            },
            FallbackStore = new StoreMetrics
            {
                Count = _fallbackStore.Count,
                Capacity = _fallbackStore.Capacity,
                IsEmpty = _fallbackStore.IsEmpty,
                IsFull = _fallbackStore.IsFull,
                DiscardedEvents = fallbackStats.TotalDiscarded,
                TotalAppended = fallbackStats.TotalAppended,
                AppendsPerSecond = fallbackStats.AppendsPerSecond,
                LastAppendTime = fallbackStats.LastAppendTime
            }
        };
    }
}

public sealed record PaymentEvent
{
    public required double Amount { get; init; }
    public required DateTime RequestedAt { get; init; }
}

// Implementação do TimestampSelector
public class PaymentEventTimestampSelector : IEventTimestampSelector<PaymentEvent>
{
    public DateTime GetTimestamp(PaymentEvent eventData) => eventData.RequestedAt;
}


[JsonSerializable(typeof(PaymentRecord))]
[JsonSerializable(typeof(PaymentEvent))]
[JsonSerializable(typeof(DatabaseSummaryResponse))]
[JsonSerializable(typeof(DatabaseSummaryOrigin))]
[JsonSerializable(typeof(MetricsResponse))]
[JsonSerializable(typeof(StoreMetrics))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DatabaseJsonSerializerContext : JsonSerializerContext { }