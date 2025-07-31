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

var builder = WebApplication.CreateBuilder(args);

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DatabaseJsonSerializerContext.Default);
});

builder.WebHost.ConfigureKestrel(options => {
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/sockets/database.sock";
    try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    
    options.ListenUnixSocket(socketPath);
    
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        _ = Task.Run(async () => {
            for (int i = 0; i < 10; i++) {
                await Task.Delay(500);
                try {
                    if (File.Exists(socketPath)) {
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

app.MapGet("/summary", (DateTime? from, DateTime? to) => {
    var (defaultSummary, fallbackSummary) = paymentService.GetSummary(from, to);
    
    var response = new SummaryResponse {
        Default = defaultSummary,
        Fallback = fallbackSummary
    };
    
    return Results.Ok(response);
});

app.MapPost("/purge-payments", () => {
    paymentService.PurgePayments();
    return Results.Ok();
});

await app.RunAsync();

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

public sealed class PaymentService
{
    private readonly ConcurrentQueue<PaymentRecord> _defaultPayments;
    private readonly ConcurrentQueue<PaymentRecord> _fallbackPayments;
    private readonly int _maxCapacity;

    private sealed class AtomicBucket
    {
        private int _count;
        private double _amount;
        public void Add(double amount)
        {
            Interlocked.Increment(ref _count);
            double initial, computed;
            do
            {
                initial = _amount;
                computed = initial + amount;
            } while (Interlocked.CompareExchange(ref _amount, computed, initial) != initial);
        }
        public int Count => _count;
        public double Amount => _amount;
    }
    private readonly ConcurrentDictionary<long, AtomicBucket> _defaultAgg = new();
    private readonly ConcurrentDictionary<long, AtomicBucket> _fallbackAgg = new();
    private int _defaultCount = 0;
    private long _defaultTotalAmountAsCents = 0;
    private int _fallbackCount = 0;
    private long _fallbackTotalAmountAsCents = 0;

    public PaymentService(int maxCapacity)
    {
        _maxCapacity = maxCapacity;
        _defaultPayments = new ConcurrentQueue<PaymentRecord>();
        _fallbackPayments = new ConcurrentQueue<PaymentRecord>();
    }

    public bool AddPayment(PaymentRecord payment, bool isFallback)
    {
        ref int count = ref isFallback ? ref _fallbackCount : ref _defaultCount;
        ref long totalAmount = ref isFallback ? ref _fallbackTotalAmountAsCents : ref _defaultTotalAmountAsCents;
        var queue = isFallback ? _fallbackPayments : _defaultPayments;

        if (Interlocked.Increment(ref count) > _maxCapacity)
        {
            Interlocked.Decrement(ref count); // Desfaz a contagem
            return false;
        }

        queue.Enqueue(payment);

        Interlocked.Add(ref totalAmount, (long)(payment.Amount * 100));

        var minute = new DateTime(payment.RequestedAt.Year, payment.RequestedAt.Month, payment.RequestedAt.Day, payment.RequestedAt.Hour, payment.RequestedAt.Minute, 0, DateTimeKind.Utc).Ticks;
        var dict = isFallback ? _fallbackAgg : _defaultAgg;
        var bucket = dict.GetOrAdd(minute, _ => new AtomicBucket());
        bucket.Add(payment.Amount);

        return true;
    }

    public (SummaryOrigin Default, SummaryOrigin Fallback) GetSummary(DateTime? from, DateTime? to)
    {        

        var fromUtc = from!.Value.ToUniversalTime();
        var toUtc = to!.Value.ToUniversalTime();
        var fromMinute = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, fromUtc.Minute, 0, DateTimeKind.Utc).Ticks;
        var toMinute = new DateTime(toUtc.Year, toUtc.Month, toUtc.Day, toUtc.Hour, toUtc.Minute, 0, DateTimeKind.Utc).Ticks;

        (int, double) SumHybrid(ConcurrentQueue<PaymentRecord> queue, ConcurrentDictionary<long, AtomicBucket> agg)
        {
            int totalCount = 0;
            double totalAmount = 0;
            foreach (var kv in agg)
            {
                if (kv.Key > fromMinute && kv.Key < toMinute)
                {
                    totalCount += kv.Value.Count;
                    totalAmount += kv.Value.Amount;
                }
            }
            foreach (var p in queue)
            {
                var minute = new DateTime(p.RequestedAt.Year, p.RequestedAt.Month, p.RequestedAt.Day, p.RequestedAt.Hour, p.RequestedAt.Minute, 0, DateTimeKind.Utc).Ticks;
                if ((minute == fromMinute || minute == toMinute) && p.RequestedAt >= fromUtc && p.RequestedAt <= toUtc)
                {
                    totalCount++;
                    totalAmount += p.Amount;
                }
            }
            return (totalCount, totalAmount);
        }

        var (defaultCount, defaultAmount) = SumHybrid(_defaultPayments, _defaultAgg);
        var (fallbackCount, fallbackAmount) = SumHybrid(_fallbackPayments, _fallbackAgg);
        return (
            new SummaryOrigin { TotalRequests = defaultCount, TotalAmount = Math.Round(defaultAmount, 2) },
            new SummaryOrigin { TotalRequests = fallbackCount, TotalAmount = Math.Round(fallbackAmount, 2) }
        );
    }

    private SummaryOrigin ProcessQueueWithFilter(ConcurrentQueue<PaymentRecord> queue, DateTime from, DateTime to)
    {
        int totalRequests = 0;
        double totalAmount = 0.0;
        
        foreach (var p in queue)
        {
            if (p.RequestedAt >= from && p.RequestedAt <= to)
            {
                totalRequests++;
                totalAmount += p.Amount;
            }
        }
        return new SummaryOrigin { TotalRequests = totalRequests, TotalAmount = Math.Round(totalAmount * 100) / 100.0 };
    }

    public void PurgePayments()
    {
        _defaultPayments.Clear();
        _fallbackPayments.Clear();
        _defaultAgg.Clear();
        _fallbackAgg.Clear();
        Volatile.Write(ref _defaultCount, 0);
        Volatile.Write(ref _defaultTotalAmountAsCents, 0);
        Volatile.Write(ref _fallbackCount, 0);
        Volatile.Write(ref _fallbackTotalAmountAsCents, 0);
    }
}


[JsonSerializable(typeof(PaymentRecord))]
[JsonSerializable(typeof(SummaryResponse))]
[JsonSerializable(typeof(SummaryOrigin))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DatabaseJsonSerializerContext : JsonSerializerContext { }