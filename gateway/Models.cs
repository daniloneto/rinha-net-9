using System.Text.Json.Serialization;

namespace Gateway;

public sealed record PaymentRequest
{
    [JsonPropertyName("correlationId")]
    public required Guid CorrelationId { get; init; }
    
    [JsonPropertyName("amount")]
    public required double Amount { get; init; }

    public PaymentProcessorRequest ToProcessor()
    {
        return new PaymentProcessorRequest
        {
            CorrelationId = CorrelationId,
            Amount = Amount,
            RequestedAt = DateTime.UtcNow
        };
    }
}

public sealed record PaymentProcessorRequest
{
    [JsonPropertyName("correlationId")]
    public required Guid CorrelationId { get; init; }
    
    [JsonPropertyName("amount")]
    public required double Amount { get; init; }
    
    [JsonPropertyName("requestedAt")]
    public required DateTime RequestedAt { get; init; }
}

public sealed record DatabasePaymentRequest
{
    [JsonPropertyName("a")]
    public required double Amount { get; init; }
    
    [JsonPropertyName("t")]
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

// Modelos internos para comunicação Gateway <-> Database (otimizados)
public sealed record DatabaseSummaryResponse
{
    [JsonPropertyName("d")]
    public required DatabaseSummaryOrigin Default { get; init; }
    
    [JsonPropertyName("f")]
    public required DatabaseSummaryOrigin Fallback { get; init; }

    public SummaryResponse ToExternal()
    {
        return new SummaryResponse
        {
            Default = new SummaryOrigin 
            { 
                TotalRequests = Default.TotalRequests, 
                TotalAmount = Default.TotalAmount 
            },
            Fallback = new SummaryOrigin 
            { 
                TotalRequests = Fallback.TotalRequests, 
                TotalAmount = Fallback.TotalAmount 
            }
        };
    }
}

public sealed record DatabaseSummaryOrigin
{
    [JsonPropertyName("c")]
    public required int TotalRequests { get; init; }
    
    [JsonPropertyName("s")]
    public required double TotalAmount { get; init; }
}

public sealed record ServiceHealthResponse
{
    [JsonPropertyName("failing")]
    public required bool Failing { get; init; }
    
    [JsonPropertyName("minResponseTime")]
    public required int MinResponseTime { get; init; }
}
