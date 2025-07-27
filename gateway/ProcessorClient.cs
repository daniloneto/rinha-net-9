using System.Text.Json;

namespace Gateway;

public class ProcessorClient
{
    private static readonly SocketsHttpHandler _handler = new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 512
    };
    private readonly HttpClient _httpClient;

    public ProcessorClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public virtual async Task<bool> CaptureDefaultAsync(PaymentProcessorRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, AppJsonSerializerContext.Default.PaymentProcessorRequest);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(Constants.DefaultProcessorUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task<bool> CaptureFallbackAsync(PaymentProcessorRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, AppJsonSerializerContext.Default.PaymentProcessorRequest);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(Constants.FallbackProcessorUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
    public async Task<ServiceHealthResponse> GetHealthAsync(string url)
    {
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var health = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ServiceHealthResponse);
        if (health == null) throw new Exception("Invalid health response");
        return health;
    }
}
