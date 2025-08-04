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
        private readonly ChannelReader<PaymentRequest> _reader;            private readonly int _workerMultiplier;
        private readonly int _retryBaseDelayMs;
        private readonly int _retryMaxDelayMs;
        private readonly int _healthCheckIntervalSeconds;
        private readonly int _maxRetriesBeforeFallback;        // Batching variables
        private readonly List<PaymentProcessorRequest> _defaultBatch = new();
        private readonly List<PaymentProcessorRequest> _fallbackBatch = new();
        private readonly Timer _batchTimer;
        private readonly int _batchSize;
        private readonly int _batchTimeoutMs;
        public PaymentService(
            ProcessorClient client,
            Repository repository)
        {
            _client = client;
            _repository = repository;
            _workerMultiplier = int.TryParse(Environment.GetEnvironmentVariable("WorkerMultiplier"), out var wm) ? wm : 1;            _retryBaseDelayMs = 25;
            _retryMaxDelayMs = 200;
            _maxRetriesBeforeFallback = 5;
            
            _healthCheckIntervalSeconds = int.TryParse(Environment.GetEnvironmentVariable("HealthCheckIntervalSeconds"), out var hcis) ? hcis : 2;
          // Batching configuration
        _batchSize = int.TryParse(Environment.GetEnvironmentVariable("BatchSize"), out var bs) ? bs : 10;
        _batchTimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("BatchTimeoutMs"), out var bt) ? bt : 50;
 
            _channel = Channel.CreateUnbounded<PaymentRequest>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });            _writer = _channel.Writer;
            _reader = _channel.Reader;            // Timer para flush de batches
            _batchTimer = new Timer(FlushBatches, null, TimeSpan.FromMilliseconds(_batchTimeoutMs), TimeSpan.FromMilliseconds(_batchTimeoutMs));

            // Health check unificado (menos overhead)
            _ = Task.Run(HealthCheckLoop);
        }        // Health check unificado - mais eficiente
        private async Task HealthCheckLoop()
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                
                // Check default
                if ((now - _lastDefaultHealthCheck).TotalSeconds >= _healthCheckIntervalSeconds)
                {
                    try
                    {
                        var health = await _client.GetHealthAsync(Constants.DefaultProcessorUrl.Replace("/payments", "/payments/service-health"));
                        _defaultHealthCache = health;
                        _defaultHealth = !health.Failing;
                        _lastDefaultHealthCheck = now;
                    }
                    catch { _defaultHealth = false; }
                }
                
                // Check fallback
                if ((now - _lastFallbackHealthCheck).TotalSeconds >= _healthCheckIntervalSeconds)
                {
                    try
                    {
                        var health = await _client.GetHealthAsync(Constants.FallbackProcessorUrl.Replace("/payments", "/payments/service-health"));
                        _fallbackHealthCache = health;
                        _fallbackHealth = !health.Failing;
                        _lastFallbackHealthCheck = now;
                    }
                    catch { _fallbackHealth = false; }
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
                        
                        if (_defaultHealth && retryCount < _maxRetriesBeforeFallback)
                        {
                            success = await _client.CaptureDefaultAsync(processorRequest);
                            if (success)
                            {
                                _ = _repository.InsertDefaultAsync(processorRequest);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }                      
                        else if (!success && _fallbackHealth && retryCount >= _maxRetriesBeforeFallback)
                        {
                            success = await _client.CaptureFallbackAsync(processorRequest);
                            if (success)
                            {
                                _ = _repository.InsertFallbackAsync(processorRequest);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }                        }
                        if (!success)
                        {
                            retryCount++;
                            _retryCounts[request.CorrelationId] = retryCount;
                            int delayMs = Math.Min(_retryBaseDelayMs * (1 << Math.Min(retryCount, 5)), _retryMaxDelayMs);
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
                          if (_defaultHealth && retryCount < _maxRetriesBeforeFallback)
                        {
                            success = await _client.CaptureDefaultAsync(processorRequest);
                            if (success)
                            {
                                // Add to batch instead of individual insert
                                AddToBatch(processorRequest, isFallback: false);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }
                        
                        else if (!success && _fallbackHealth && retryCount >= _maxRetriesBeforeFallback)
                        {
                            success = await _client.CaptureFallbackAsync(processorRequest);
                            if (success)
                            {
                                // Add to batch instead of individual insert
                                AddToBatch(processorRequest, isFallback: true);
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
                });            }
        }

        private void AddToBatch(PaymentProcessorRequest request, bool isFallback)
        {
            lock (isFallback ? _fallbackBatch : _defaultBatch)
            {
                var batch = isFallback ? _fallbackBatch : _defaultBatch;
                batch.Add(request);
                
                // Flush if batch is full
                if (batch.Count >= _batchSize)
                {
                    _ = Task.Run(() => FlushBatch(isFallback));
                }
            }
        }

        private void FlushBatches(object? state)
        {
            _ = Task.Run(() => FlushBatch(isFallback: false));
            _ = Task.Run(() => FlushBatch(isFallback: true));
        }

        private async Task FlushBatch(bool isFallback)
        {
            List<PaymentProcessorRequest> batchToFlush;
            
            lock (isFallback ? _fallbackBatch : _defaultBatch)
            {
                var batch = isFallback ? _fallbackBatch : _defaultBatch;
                if (batch.Count == 0) return;
                
                batchToFlush = new List<PaymentProcessorRequest>(batch);
                batch.Clear();
            }

            try
            {
                if (isFallback)
                    await _repository.InsertFallbackBatchAsync(batchToFlush);
                else
                    await _repository.InsertDefaultBatchAsync(batchToFlush);
            }
            catch
            {
                // Em caso de erro, volta para inserção individual como fallback
                foreach (var request in batchToFlush)
                {
                    try
                    {
                        if (isFallback)
                            await _repository.InsertFallbackAsync(request);
                        else
                            await _repository.InsertDefaultAsync(request);
                    }
                    catch { /* Ignore individual failures */ }
                }
            }
        }

    }
}
