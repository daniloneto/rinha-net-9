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

public sealed record ServiceHealthResponse
{
    [JsonPropertyName("failing")]
    public required bool Failing { get; init; }
    
    [JsonPropertyName("minResponseTime")]
    public required int MinResponseTime { get; init; }
}
