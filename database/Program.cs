using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime;
using System.Net.Sockets;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
ThreadPool.SetMinThreads(8, 8);
ThreadPool.SetMaxThreads(16, 16);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DatabaseJsonSerializerContext.Default);
});

builder.WebHost.ConfigureKestrel(options =>
{
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/sockets/database.sock";
    try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    options.ListenUnixSocket(socketPath);
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
                    });
                    process?.WaitForExit();
                    break;
                }
            }
            catch { }
        }
    });
});

var app = builder.Build();
Console.WriteLine("C# Database Service");



var defaultPayments = new RingBuffer<PaymentRecord>(65536);
var fallbackPayments = new RingBuffer<PaymentRecord>(65536);

app.MapPost("/payments/default", (PaymentRecord payment) =>
{
    if (payment.Amount <= 0 || payment.RequestedAt == default)
        return Results.BadRequest("'amount' deve ser positivo e 'requestedAt' deve ser uma data válida.");
    try
    {
        defaultPayments.Enqueue(payment);
        return Results.Created($"/payments/default/{Guid.NewGuid()}", null);
    }
    catch (Exception)
    {
        return Results.StatusCode(500);
    }
});

app.MapPost("/payments/fallback", (PaymentRecord payment) =>
{
    if (payment.Amount <= 0 || payment.RequestedAt == default)
        return Results.BadRequest("'amount' deve ser positivo e 'requestedAt' deve ser uma data válida.");
    try
    {
        fallbackPayments.Enqueue(payment);
        return Results.Created($"/payments/fallback/{Guid.NewGuid()}", null);
    }
    catch (Exception)
    {
        return Results.StatusCode(500);
    }
});

app.MapGet("/summary", (DateTime? from, DateTime? to) =>
{
    var defaultList = defaultPayments.ToArray();
    var fallbackList = fallbackPayments.ToArray();
    int defaultRequests = 0;
    double defaultAmount = 0.0;
    int fallbackRequests = 0;
    double fallbackAmount = 0.0;
    if (from.HasValue && to.HasValue)
    {
        foreach (var p in defaultList)
            if (p.RequestedAt >= from.Value && p.RequestedAt <= to.Value)
            {
                defaultRequests++;
                defaultAmount += p.Amount;
            }
        foreach (var p in fallbackList)
            if (p.RequestedAt >= from.Value && p.RequestedAt <= to.Value)
            {
                fallbackRequests++;
                fallbackAmount += p.Amount;
            }
    }
    else
    {
        defaultRequests = defaultList.Length;
        defaultAmount = defaultList.Sum(p => p.Amount);
        fallbackRequests = fallbackList.Length;
        fallbackAmount = fallbackList.Sum(p => p.Amount);
    }
    var defaultSummary = new SummaryOrigin
    {
        TotalRequests = defaultRequests,
        TotalAmount = Math.Round(defaultAmount * 100.0) / 100.0
    };
    var fallbackSummary = new SummaryOrigin
    {
        TotalRequests = fallbackRequests,
        TotalAmount = Math.Round(fallbackAmount * 100.0) / 100.0
    };
    var response = new SummaryResponse
    {
        Default = defaultSummary,
        Fallback = fallbackSummary
    };
    return Results.Ok(response);
});

app.MapPost("/purge-payments", () =>
{
    defaultPayments.Clear();
    fallbackPayments.Clear();
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
// RingBuffer lock-free para pagamentos
public class RingBuffer<T>
{
    private readonly T[] _buffer;
    private int _writeIndex = 0;
    private int _count = 0;
    private readonly int _capacity;
    private readonly object _lock = new object();

    public RingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new T[capacity];
    }

    public void Enqueue(T item)
    {
        lock (_lock)
        {
            _buffer[_writeIndex] = item;
            _writeIndex = (_writeIndex + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    public T[] ToArray()
    {
        lock (_lock)
        {
            T[] result = new T[_count];
            int start = (_writeIndex - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % _capacity];
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writeIndex = 0;
            _count = 0;
        }
    }
}

[JsonSerializable(typeof(PaymentRecord))]
[JsonSerializable(typeof(SummaryResponse))]
[JsonSerializable(typeof(SummaryOrigin))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DatabaseJsonSerializerContext : JsonSerializerContext
{
}
