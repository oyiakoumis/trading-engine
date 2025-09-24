using TradingEngine.Execution.Pipeline.Stages;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.Domain.Events;
using System.Collections.Concurrent;

namespace TradingEngine.Execution.Adapters
{
    /// <summary>
    /// Simple rate limiter implementation
    /// </summary>
    public sealed class SimpleRateLimiter : IRateLimiter
    {
        private readonly int _maxRequestsPerMinute;
        private readonly Queue<DateTime> _requestTimes = new();
        private readonly object _lock = new();

        public SimpleRateLimiter(int maxRequestsPerMinute)
        {
            _maxRequestsPerMinute = maxRequestsPerMinute;
        }

        public ValueTask WaitAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var cutoff = now.AddMinutes(-1);

                // Remove old entries
                while (_requestTimes.Count > 0 && _requestTimes.Peek() < cutoff)
                {
                    _requestTimes.Dequeue();
                }

                if (_requestTimes.Count < _maxRequestsPerMinute)
                {
                    _requestTimes.Enqueue(now);
                    return ValueTask.CompletedTask;
                }
            }

            // Rate limit exceeded - wait a bit
            return new ValueTask(Task.Delay(100, cancellationToken));
        }

        public bool TryAcquire()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var cutoff = now.AddMinutes(-1);

                while (_requestTimes.Count > 0 && _requestTimes.Peek() < cutoff)
                {
                    _requestTimes.Dequeue();
                }

                if (_requestTimes.Count < _maxRequestsPerMinute)
                {
                    _requestTimes.Enqueue(now);
                    return true;
                }

                return false;
            }
        }

        public ValueTask<RateLimiterHealth> GetHealthAsync()
        {
            lock (_lock)
            {
                return ValueTask.FromResult(new RateLimiterHealth
                {
                    IsHealthy = true,
                    CurrentRequests = _requestTimes.Count,
                    MaxRequests = _maxRequestsPerMinute,
                    WindowSize = TimeSpan.FromMinutes(1),
                    WindowStart = DateTime.UtcNow.AddMinutes(-1)
                });
            }
        }
    }

    /// <summary>
    /// Simple bulkhead isolation implementation
    /// </summary>
    public sealed class SimpleBulkheadIsolation : IBulkheadIsolation
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxConcurrentExecutions;
        private int _activeExecutions;

        public SimpleBulkheadIsolation(int maxConcurrentExecutions)
        {
            _maxConcurrentExecutions = maxConcurrentExecutions;
            _semaphore = new SemaphoreSlim(maxConcurrentExecutions, maxConcurrentExecutions);
        }

        public async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation)
        {
            await _semaphore.WaitAsync();
            Interlocked.Increment(ref _activeExecutions);
            
            try
            {
                return await operation();
            }
            finally
            {
                Interlocked.Decrement(ref _activeExecutions);
                _semaphore.Release();
            }
        }

        public BulkheadHealth GetHealth()
        {
            return new BulkheadHealth
            {
                IsHealthy = _activeExecutions < _maxConcurrentExecutions,
                ActiveExecutions = _activeExecutions,
                MaxConcurrentExecutions = _maxConcurrentExecutions,
                QueuedRequests = Math.Max(0, _maxConcurrentExecutions - _semaphore.CurrentCount),
                MaxQueueSize = _maxConcurrentExecutions
            };
        }
    }

    /// <summary>
    /// Simple in-memory metrics collector
    /// </summary>
    public sealed class InMemoryMetricsCollector : IMetricsCollector
    {
        private readonly ConcurrentDictionary<Symbol, SymbolMetrics> _symbolMetrics = new();

        public ValueTask RecordProcessingLatencyAsync(Symbol symbol, TimeSpan latency)
        {
            var metrics = _symbolMetrics.GetOrAdd(symbol, _ => new SymbolMetrics());
            metrics.RecordLatency(latency);
            return ValueTask.CompletedTask;
        }

        public ValueTask IncrementOrdersProcessedAsync(Symbol symbol)
        {
            var metrics = _symbolMetrics.GetOrAdd(symbol, _ => new SymbolMetrics());
            metrics.IncrementOrdersProcessed();
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordRiskLevelAsync(Symbol symbol, Pipeline.Models.RiskLevel riskLevel)
        {
            var metrics = _symbolMetrics.GetOrAdd(symbol, _ => new SymbolMetrics());
            metrics.RecordRiskLevel(riskLevel);
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordSignalConfidenceAsync(Symbol symbol, double confidence)
        {
            var metrics = _symbolMetrics.GetOrAdd(symbol, _ => new SymbolMetrics());
            metrics.RecordConfidence(confidence);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsHealthyAsync()
        {
            return ValueTask.FromResult(true);
        }

        private sealed class SymbolMetrics
        {
            private long _ordersProcessed;
            private long _totalLatencyMs;

            public void RecordLatency(TimeSpan latency)
            {
                Interlocked.Add(ref _totalLatencyMs, (long)latency.TotalMilliseconds);
            }

            public void IncrementOrdersProcessed()
            {
                Interlocked.Increment(ref _ordersProcessed);
            }

            public void RecordRiskLevel(Pipeline.Models.RiskLevel riskLevel)
            {
                // Could store risk level statistics here
            }

            public void RecordConfidence(double confidence)
            {
                // Could store confidence statistics here
            }
        }
    }

    /// <summary>
    /// Simple event bus adapter
    /// </summary>
    public sealed class SimpleEventBusAdapter : Pipeline.Stages.IEventBus
    {
        private readonly TradingEngine.Infrastructure.EventBus.IEventBus _eventBus;

        public SimpleEventBusAdapter(TradingEngine.Infrastructure.EventBus.IEventBus eventBus)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IEvent
        {
            await _eventBus.PublishAsync(@event, cancellationToken);
        }

        public Pipeline.Stages.EventBusStatistics GetStatistics()
        {
            var stats = _eventBus.GetStatistics();
            return new Pipeline.Stages.EventBusStatistics
            {
                TotalEventsPublished = stats.TotalEventsPublished,
                TotalEventsFailed = stats.TotalEventsFailed,
                ActiveSubscriptions = stats.ActiveSubscriptions
            };
        }
    }
}