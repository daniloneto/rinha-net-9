using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Gateway;

public class Repository
{
    private readonly HttpClient _httpClient;

    public Repository(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }


    public virtual async Task<bool> InsertDefaultAsync(PaymentProcessorRequest request)
    {
        try
        {
            var dbRequest = new DatabasePaymentRequest
            {
                Amount = request.Amount,
                RequestedAt = request.RequestedAt
            };
            var json = JsonSerializer.Serialize(dbRequest, AppJsonSerializerContext.Default.DatabasePaymentRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            _httpClient.PostAsync("/payments/default", content); // fire-and-forget
            return await Task.FromResult(true);
        }
        catch
        {           
            return await Task.FromResult(false);
        }
    }

    public virtual async Task<bool> InsertFallbackAsync(PaymentProcessorRequest request)
    {
        try
        {
            var dbRequest = new DatabasePaymentRequest
            {
                Amount = request.Amount,
                RequestedAt = request.RequestedAt
            };
            var json = JsonSerializer.Serialize(dbRequest, AppJsonSerializerContext.Default.DatabasePaymentRequest);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            _httpClient.PostAsync("/payments/fallback", content); // fire-and-forget
            return await Task.FromResult(true);
        }
        catch
        {           
            return await Task.FromResult(false);
        }
    }

    public virtual async Task<bool> PurgePaymentsAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("/purge-payments", null);
            return response.IsSuccessStatusCode;
        }
        catch
        {            
            return false;
        }
    }

    public virtual async Task<SummaryResponse> GetSummaryAsync(DateTime? from, DateTime? to)
    {
        try
        {
            var endpoint = "/summary";
            if (from.HasValue && to.HasValue)
            {
                var fromStr = from.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var toStr = to.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                endpoint = $"/summary?from={fromStr}&to={toStr}";
            }

            for (int i = 0; i < 3; i++)
            {
                var response = await _httpClient.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var summary = JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.SummaryResponse);
                    if (summary != null)
                        return summary;
                }
                await Task.Delay(100);
            }
           
            return new SummaryResponse
            {
                Default = new SummaryOrigin { TotalRequests = 0, TotalAmount = 0 },
                Fallback = new SummaryOrigin { TotalRequests = 0, TotalAmount = 0 }
            };
        }
        catch
        {           
            return new SummaryResponse
            {
                Default = new SummaryOrigin { TotalRequests = 0, TotalAmount = 0 },
                Fallback = new SummaryOrigin { TotalRequests = 0, TotalAmount = 0 }
            };
        }
    }
}
