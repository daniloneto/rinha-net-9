using System.Net;
using System.Runtime;
using System.Text.Json;
using System;
using Gateway;

var builder = WebApplication.CreateBuilder(args);

GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
ThreadPool.SetMinThreads(8, 8); 
ThreadPool.SetMaxThreads(16, 16); 

// Configure JSON options
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Configure Kestrel for Unix socket
builder.WebHost.ConfigureKestrel(options =>
{    var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/tmp/gateway-1.sock";
    
    // Remove existing socket if it exists
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
                    });                    process?.WaitForExit();
                    
                    break;
                }
            }            catch
            {
               
            }
        }
    });
});

var httpClient = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    MaxConnectionsPerServer = 512 // Aumenta concorrência para stress máximo
});

var dbHttpClient = new HttpClient(new UnixDomainSocketHttpHandler("/sockets/database.sock"))
{
    BaseAddress = new Uri("http://localhost/"),
    Timeout = TimeSpan.FromSeconds(5)
};

var repository = new Repository(dbHttpClient);
var controller = new Controller(repository);
var processorClient = new ProcessorClient(httpClient);
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

public partial class Program { }
