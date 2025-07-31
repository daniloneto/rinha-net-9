using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Gateway
{
    public class PaymentService
    {

        private readonly ConcurrentDictionary<Guid, int> _retryCounts = new();
        private readonly ProcessorClient _client;
        private readonly Repository _repository;
        private volatile bool _defaultHealth = false;
        private volatile bool _fallbackHealth = false;
        private ServiceHealthResponse? _defaultHealthCache;
        private ServiceHealthResponse? _fallbackHealthCache;
        private DateTime _lastDefaultHealthCheck = DateTime.MinValue;
        private DateTime _lastFallbackHealthCheck = DateTime.MinValue;

        private readonly ConcurrentQueue<PaymentRequest> _queue = new();
        private readonly Channel<PaymentRequest> _channel;
        private readonly ChannelWriter<PaymentRequest> _writer;
        private readonly ChannelReader<PaymentRequest> _reader;        

        private readonly int _workerMultiplier;
        private readonly int _retryBaseDelayMs;
        private readonly int _retryMaxDelayMs;
        private readonly int _healthCheckIntervalSeconds;

        public PaymentService(
            ProcessorClient client,
            Repository repository,
            int workerMultiplier,
            int retryBaseDelayMs,
            int retryMaxDelayMs,
            int healthCheckIntervalSeconds)
        {
            _client = client;
            _repository = repository;
            _workerMultiplier = workerMultiplier;
            _retryBaseDelayMs = retryBaseDelayMs;
            _retryMaxDelayMs = retryMaxDelayMs;
            _healthCheckIntervalSeconds = healthCheckIntervalSeconds;

            _channel = Channel.CreateUnbounded<PaymentRequest>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
            _writer = _channel.Writer;
            _reader = _channel.Reader;

            _ = Task.Run(HealthCheckLoopDefault);
            _ = Task.Run(HealthCheckLoopFallback);
        }
        private async Task HealthCheckLoopDefault()
        {
            while (true)
            {
                if ((DateTime.UtcNow - _lastDefaultHealthCheck).TotalSeconds >= _healthCheckIntervalSeconds)
                {
                    try
                    {
                        var health = await _client.GetHealthAsync(Constants.DefaultProcessorUrl.Replace("/payments", "/payments/service-health"));
                        _defaultHealthCache = health;
                        _defaultHealth = !health.Failing;
                    }
                    catch { }
                    _lastDefaultHealthCheck = DateTime.UtcNow;
                }
                await Task.Delay(1000);
            }
        }

        private async Task HealthCheckLoopFallback()
        {
            while (true)
            {
                if ((DateTime.UtcNow - _lastFallbackHealthCheck).TotalSeconds >= _healthCheckIntervalSeconds)
                {
                    try
                    {
                        var health = await _client.GetHealthAsync(Constants.FallbackProcessorUrl.Replace("/payments", "/payments/service-health"));
                        _fallbackHealthCache = health;
                        _fallbackHealth = !health.Failing;
                    }
                    catch { }
                    _lastFallbackHealthCheck = DateTime.UtcNow;
                }
                await Task.Delay(1000);
            }
        }

        public void Submit(PaymentRequest request)
        {
            _writer.TryWrite(request);
        }

        public void InitializeDispatcher()
        {
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync())
                {
                    if (_queue.TryDequeue(out var request))
                    {
                        var processorRequest = request.ToProcessor();
                        bool success = false;
                        int retryCount = _retryCounts.GetOrAdd(request.CorrelationId, 0);
                        if (_defaultHealth)
                        {
                            success = await _client.CaptureDefaultAsync(processorRequest);
                            if (success)
                            {
                                _ = _repository.InsertDefaultAsync(processorRequest);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }
                        if (!success && _fallbackHealth)
                        {
                            success = await _client.CaptureFallbackAsync(processorRequest);
                            if (success)
                            {
                                _ = _repository.InsertFallbackAsync(processorRequest);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }
                        if (!success)
                        {
                            retryCount++;
                            _retryCounts[request.CorrelationId] = retryCount;
                            int delayMs = Math.Min(_retryBaseDelayMs * (1 << Math.Min(retryCount, 5)), _retryMaxDelayMs); // Exponential backoff, max configurable
                            await Task.Delay(delayMs);
                            _queue.Enqueue(request);
                        }
                        else
                        {
                            // Process queue in batch
                            while (_queue.TryDequeue(out var queuedItem))
                            {
                                if (!_writer.TryWrite(queuedItem))
                                    break;
                            }
                        }
                    }
                }
            });
        }

        public void InitializeWorkers()
        {
            int workerCount = Environment.ProcessorCount * _workerMultiplier;
            for (int i = 0; i < workerCount; i++)
            {
                _ = Task.Run(async () =>
                {
                    await foreach (var request in _reader.ReadAllAsync())
                    {
                        var processorRequest = request.ToProcessor();
                        bool success = false;
                        int retryCount = _retryCounts.GetOrAdd(request.CorrelationId, 0);
                        if (_defaultHealth)
                        {
                            success = await _client.CaptureDefaultAsync(processorRequest);
                            if (success)
                            {
                                _ = _repository.InsertDefaultAsync(processorRequest);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }
                        if (!success && _fallbackHealth)
                        {
                            success = await _client.CaptureFallbackAsync(processorRequest);
                            if (success)
                            {
                                _ = _repository.InsertFallbackAsync(processorRequest);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }
                        if (!success)
                        {
                            retryCount++;
                            _retryCounts[request.CorrelationId] = retryCount;
                            int delayMs = Math.Min(_retryBaseDelayMs * (1 << Math.Min(retryCount, 5)), _retryMaxDelayMs);
                            await Task.Delay(delayMs);
                            _queue.Enqueue(request);
                        }
                    }
                });
            }
        }

    }
}
