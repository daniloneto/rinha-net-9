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
        private readonly int _maxRetriesBeforeFallback;        // Batching variables - lock-free para melhor performance
        private readonly ConcurrentQueue<PaymentProcessorRequest> _defaultBatch = new();
        private readonly ConcurrentQueue<PaymentProcessorRequest> _fallbackBatch = new();
        private volatile int _defaultBatchCount = 0;
        private volatile int _fallbackBatchCount = 0;
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
                                // Usar batching também no dispatcher para consistência
                                AddToBatch(processorRequest, isFallback: false);
                                _retryCounts.TryRemove(request.CorrelationId, out _);
                            }
                        }                      
                        else if (!success && _fallbackHealth && retryCount >= _maxRetriesBeforeFallback)
                        {
                            success = await _client.CaptureFallbackAsync(processorRequest);
                            if (success)
                            {
                                // Usar batching também no dispatcher para consistência
                                AddToBatch(processorRequest, isFallback: true);
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
        }        private void AddToBatch(PaymentProcessorRequest request, bool isFallback)
        {
            // Lock-free enqueue
            if (isFallback)
            {
                _fallbackBatch.Enqueue(request);
                var count = Interlocked.Increment(ref _fallbackBatchCount);
                
                // Flush apenas se necessário, sem Task.Run overhead
                if (count >= _batchSize)
                {
                    _ = FlushBatch(isFallback);
                }
            }
            else
            {
                _defaultBatch.Enqueue(request);
                var count = Interlocked.Increment(ref _defaultBatchCount);
                
                if (count >= _batchSize)
                {
                    _ = FlushBatch(isFallback);
                }
            }
        }        private void FlushBatches(object? state)
        {
            // Execução direta sem Task.Run overhead
            _ = FlushBatch(isFallback: false);
            _ = FlushBatch(isFallback: true);
        }        private async Task FlushBatch(bool isFallback)
        {
            var batch = isFallback ? _fallbackBatch : _defaultBatch;
            var batchCount = isFallback ? ref _fallbackBatchCount : ref _defaultBatchCount;
            
            var batchToFlush = new List<PaymentProcessorRequest>();
            
            // Dequeue sem lock - mais eficiente
            while (batchToFlush.Count < _batchSize && batch.TryDequeue(out var item))
            {
                batchToFlush.Add(item);
                Interlocked.Decrement(ref batchCount);
            }
            
            if (batchToFlush.Count == 0) return;

            try
            {
                if (isFallback)
                    await _repository.InsertFallbackBatchAsync(batchToFlush);
                else
                    await _repository.InsertDefaultBatchAsync(batchToFlush);
            }
            catch
            {
                // Fallback para inserção individual
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
