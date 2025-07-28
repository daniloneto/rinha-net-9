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

var paymentService = new PaymentService(100_000);

app.MapPost("/payments/default", (PaymentRecord payment) => ProcessPayment(payment, isFallback: false));
app.MapPost("/payments/fallback", (PaymentRecord payment) => ProcessPayment(payment, isFallback: true));

IResult ProcessPayment(PaymentRecord payment, bool isFallback)
{
    if (payment.Amount <= 0 || payment.RequestedAt == default)
        return Results.BadRequest("'amount' deve ser positivo e 'requestedAt' deve ser uma data válida.");
    
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

// Classes otimizadas
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
    private readonly ConcurrentQueue<PaymentRecord> _defaultPayments = new();
    private readonly ConcurrentQueue<PaymentRecord> _fallbackPayments = new();
    private readonly int _maxCapacity;

    // Contadores e agregadores atômicos para default
    private int _defaultCount = 0;
    private long _defaultTotalAmountAsCents = 0;

    // Contadores e agregadores atômicos para fallback
    private int _fallbackCount = 0;
    private long _fallbackTotalAmountAsCents = 0;

    public PaymentService(int maxCapacity) => _maxCapacity = maxCapacity;

    public bool AddPayment(PaymentRecord payment, bool isFallback)
    {
        // Escolhe as variáveis atômicas corretas para trabalhar
        ref int count = ref isFallback ? ref _fallbackCount : ref _defaultCount;
        ref long totalAmount = ref isFallback ? ref _fallbackTotalAmountAsCents : ref _defaultTotalAmountAsCents;
        var queue = isFallback ? _fallbackPayments : _defaultPayments;

        // 1. Verifica a capacidade de forma LOCK-FREE
        if (Interlocked.Increment(ref count) > _maxCapacity)
        {
            Interlocked.Decrement(ref count); // Desfaz a contagem
            return false;
        }

        // 2. Adiciona à fila concorrente (operação otimizada e paralelizável)
        queue.Enqueue(payment);

        // 3. Atualiza o valor total de forma LOCK-FREE
        Interlocked.Add(ref totalAmount, (long)(payment.Amount * 100));

        return true;
    }

    public (SummaryOrigin Default, SummaryOrigin Fallback) GetSummary(DateTime? from, DateTime? to)
    {
        // Cenário sem filtro: usa os agregadores atômicos. Extremamente rápido e O(1).
        if (!from.HasValue && !to.HasValue)
        {
            return (
                new SummaryOrigin {
                    TotalRequests = Volatile.Read(ref _defaultCount),
                    TotalAmount = Volatile.Read(ref _defaultTotalAmountAsCents) / 100.0
                },
                new SummaryOrigin {
                    TotalRequests = Volatile.Read(ref _fallbackCount),
                    TotalAmount = Volatile.Read(ref _fallbackTotalAmountAsCents) / 100.0
                }
            );
        }
        
        // Cenário com filtro: percorre as filas. Inevitavelmente mais lento, mas sem locks globais.
        return (
            ProcessQueueWithFilter(_defaultPayments, from!.Value, to!.Value),
            ProcessQueueWithFilter(_fallbackPayments, from!.Value, to!.Value)
        );
    }

    private SummaryOrigin ProcessQueueWithFilter(ConcurrentQueue<PaymentRecord> queue, DateTime from, DateTime to)
    {
        int totalRequests = 0;
        double totalAmount = 0.0;
        
        // A iteração sobre ConcurrentQueue é thread-safe.
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
        
        Volatile.Write(ref _defaultCount, 0);
        Volatile.Write(ref _defaultTotalAmountAsCents, 0);
        Volatile.Write(ref _fallbackCount, 0);
        Volatile.Write(ref _fallbackTotalAmountAsCents, 0);
    }
}
public sealed class PaymentStorage
{
    private readonly PaymentRecord[] _buffer;
    private volatile int _head = 0;
    private volatile int _count = 0;
    private readonly int _capacity;
    private readonly object _syncRoot = new();
    private long _totalRequests = 0;
    private long _totalAmount = 0;

    public PaymentStorage(int capacity)
    {
        _capacity = capacity;
        _buffer = new PaymentRecord[capacity];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(PaymentRecord item)
    {
        lock (_syncRoot)
        {
            if (_count == _capacity)
                return false;

            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            _count++;
            
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Add(ref _totalAmount, (long)(item.Amount * 100));
            return true;
        }
    }

    public SummaryOrigin GetSummary(DateTime? from, DateTime? to)
    {
        // Caso sem filtro - uso de contadores atômicos
        if (!from.HasValue && !to.HasValue)
        {
            return new SummaryOrigin {
                TotalRequests = (int)Interlocked.Read(ref _totalRequests),
                TotalAmount = Interlocked.Read(ref _totalAmount) / 100.0
            };
        }

        // Caso com filtro - processamento otimizado
        int totalRequests = 0;
        long totalAmount = 0;
        
        lock (_syncRoot)
        {
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                var payment = _buffer[(start + i) % _capacity];
                if (payment.RequestedAt >= from.GetValueOrDefault() && payment.RequestedAt <= to.GetValueOrDefault())
                {
                    totalRequests++;
                    totalAmount += (long)(payment.Amount * 100);
                }
            }
        }

        return new SummaryOrigin {
            TotalRequests = totalRequests,
            TotalAmount = totalAmount / 100.0
        };
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _totalAmount, 0);
        }
    }
}

[JsonSerializable(typeof(PaymentRecord))]
[JsonSerializable(typeof(SummaryResponse))]
[JsonSerializable(typeof(SummaryOrigin))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DatabaseJsonSerializerContext : JsonSerializerContext { }