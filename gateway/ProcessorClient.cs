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

    /// <summary>
    /// Executa warm-up das conexões HTTP realizando chamadas de health check
    /// </summary>
    public async Task WarmupAsync()
    {
        var warmupTasks = new[]
        {
            WarmupEndpoint(Constants.DefaultProcessorUrl.Replace("/payments", "/payments/service-health"), "Default"),
            WarmupEndpoint(Constants.FallbackProcessorUrl.Replace("/payments", "/payments/service-health"), "Fallback")
        };
        
        await Task.WhenAll(warmupTasks);
    }

    private async Task WarmupEndpoint(string healthUrl, string processorName)
    {
        const int maxAttempts = 3;
        const int delayBetweenAttempts = 500; // ms
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var health = await GetHealthAsync(healthUrl);
                Console.WriteLine($"[Warm-up] {processorName} conectado (Failing: {health.Failing}, MinResponseTime: {health.MinResponseTime}ms)");
                break;
            }
            catch (Exception ex)
            {
                if (attempt < maxAttempts)
                {
                    Console.WriteLine($"[Warm-up] Tentativa {attempt} falhou para {processorName}, tentando novamente...");
                    await Task.Delay(delayBetweenAttempts);
                }
                else
                {
                    Console.WriteLine($"[Warm-up] Falha ao conectar com {processorName}: {ex.Message}");
                }
            }
        }
    }
}
