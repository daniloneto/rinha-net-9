using System.Net;
using System.Runtime;
using System.Text.Json;
using System;
using Gateway;
using UnixDomainSockets.HttpClient;

var builder = WebApplication.CreateBuilder(args);

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;


builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Configure Kestrel for Unix socket
builder.WebHost.ConfigureKestrel(options =>
{
    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/tmp/gateway-1.sock";

    try
    {
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
    }
    catch
    {

    }
    
    options.ListenUnixSocket(socketPath);

    // Otimizações conservadoras de performance
    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.MaxRequestBodySize = 1024; // 1KB - payloads pequenos
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

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
                    }); process?.WaitForExit();

                    break;
                }
            }
            catch
            {

            }
        }
    });
});


var maxConnectionsPerServer = int.TryParse(Environment.GetEnvironmentVariable("MaxConnectionsPerServer"), out var mcs) ? mcs : 256;
var pooledConnectionLifetimeMinutes = int.TryParse(Environment.GetEnvironmentVariable("PooledConnectionLifetimeMinutes"), out var pclm) ? pclm : 5;
var dbTimeoutSeconds = 1; // Timeout mais agressivo para database local

var httpClient = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(pooledConnectionLifetimeMinutes),
    MaxConnectionsPerServer = maxConnectionsPerServer
});

var dbHttpClient = UnixHttpClientFactory.For("/sockets/database.sock", TimeSpan.FromSeconds(dbTimeoutSeconds));
dbHttpClient.BaseAddress = new Uri("http://localhost/");

var repository = new Repository(dbHttpClient);
var controller = new Controller(repository);
var processorClient = new ProcessorClient(httpClient);

// Warm-up assíncrono das conexões HTTP para reduzir latência do cold start
_ = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("[Warm-up] Iniciando aquecimento das conexões HTTP...");
        await processorClient.WarmupAsync();
        Console.WriteLine("[Warm-up] Aquecimento concluído");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Warm-up] Erro durante aquecimento: {ex.Message}");
    }
});

var paymentService = new PaymentService(processorClient, repository);

var app = builder.Build();

Console.WriteLine("VERSION: 1.0 - C# .NET 9 AOT");

paymentService.InitializeDispatcher();
paymentService.InitializeWorkers();

app.MapPost("/payments", (PaymentRequest request) =>
{
    paymentService.Submit(request);
    return Results.Accepted();
});

app.MapPost("/purge-payments", async () =>
{
    await controller.PurgePaymentsAsync();
    return Results.Ok();
});

app.MapGet("/payments-summary", async (DateTime? from, DateTime? to) =>
{
    var summary = await controller.GetSummaryAsync(from, to);
    return Results.Ok(summary);
});

var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/tmp/gateway-1.sock";
if (File.Exists(socketPath) && OperatingSystem.IsLinux())
{
    File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                     UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
}

await app.RunAsync();
