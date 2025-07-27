using System.Text.Json.Serialization;

namespace Gateway;

[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentProcessorRequest))]
[JsonSerializable(typeof(DatabasePaymentRequest))]
[JsonSerializable(typeof(SummaryResponse))]
[JsonSerializable(typeof(SummaryOrigin))]
[JsonSerializable(typeof(ServiceHealthResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
