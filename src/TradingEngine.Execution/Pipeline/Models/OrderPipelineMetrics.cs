using System.Diagnostics;

namespace TradingEngine.Execution.Pipeline.Models
{
    /// <summary>
    /// Performance metrics for order processing pipeline
    /// Thread-safe metrics collection with atomic operations
    /// </summary>
    public sealed class OrderPipelineMetrics
    {
        private long _totalProcessed;
        private long _totalSuccessful;
        private long _totalFailed;
        private long _totalProcessingTimeMs;
        private long _minProcessingTimeMs = long.MaxValue;
        private long _maxProcessingTimeMs = long.MinValue;
        private readonly object _lockObject = new();

        public long TotalProcessed => _totalProcessed;
        public long TotalSuccessful => _totalSuccessful;
        public long TotalFailed => _totalFailed;
        public decimal SuccessRate => _totalProcessed == 0 ? 0 : (decimal)_totalSuccessful / _totalProcessed;
        public decimal FailureRate => _totalProcessed == 0 ? 0 : (decimal)_totalFailed / _totalProcessed;
        public double AverageProcessingTimeMs => _totalProcessed == 0 ? 0 : (double)_totalProcessingTimeMs / _totalProcessed;
        public long MinProcessingTimeMs => _minProcessingTimeMs == long.MaxValue ? 0 : _minProcessingTimeMs;
        public long MaxProcessingTimeMs => _maxProcessingTimeMs == long.MinValue ? 0 : _maxProcessingTimeMs;
        public DateTime StartTime { get; } = DateTime.UtcNow;
        public TimeSpan Uptime => DateTime.UtcNow - StartTime;

        /// <summary>
        /// Record successful processing
        /// </summary>
        public void RecordSuccess(TimeSpan processingTime)
        {
            var processingTimeMs = (long)processingTime.TotalMilliseconds;
            
            Interlocked.Increment(ref _totalProcessed);
            Interlocked.Increment(ref _totalSuccessful);
            Interlocked.Add(ref _totalProcessingTimeMs, processingTimeMs);
            
            // Update min/max with thread safety
            lock (_lockObject)
            {
                if (processingTimeMs < _minProcessingTimeMs)
                    _minProcessingTimeMs = processingTimeMs;
                
                if (processingTimeMs > _maxProcessingTimeMs)
                    _maxProcessingTimeMs = processingTimeMs;
            }
        }

        /// <summary>
        /// Record failed processing
        /// </summary>
        public void RecordFailure(TimeSpan processingTime)
        {
            var processingTimeMs = (long)processingTime.TotalMilliseconds;
            
            Interlocked.Increment(ref _totalProcessed);
            Interlocked.Increment(ref _totalFailed);
            Interlocked.Add(ref _totalProcessingTimeMs, processingTimeMs);
            
            lock (_lockObject)
            {
                if (processingTimeMs < _minProcessingTimeMs)
                    _minProcessingTimeMs = processingTimeMs;
                
                if (processingTimeMs > _maxProcessingTimeMs)
                    _maxProcessingTimeMs = processingTimeMs;
            }
        }

        /// <summary>
        /// Reset all metrics
        /// </summary>
        public void Reset()
        {
            lock (_lockObject)
            {
                _totalProcessed = 0;
                _totalSuccessful = 0;
                _totalFailed = 0;
                _totalProcessingTimeMs = 0;
                _minProcessingTimeMs = long.MaxValue;
                _maxProcessingTimeMs = long.MinValue;
            }
        }

        /// <summary>
        /// Get snapshot of current metrics
        /// </summary>
        public OrderPipelineMetricsSnapshot GetSnapshot()
        {
            lock (_lockObject)
            {
                return new OrderPipelineMetricsSnapshot
                {
                    TotalProcessed = _totalProcessed,
                    TotalSuccessful = _totalSuccessful,
                    TotalFailed = _totalFailed,
                    SuccessRate = SuccessRate,
                    FailureRate = FailureRate,
                    AverageProcessingTimeMs = AverageProcessingTimeMs,
                    MinProcessingTimeMs = MinProcessingTimeMs,
                    MaxProcessingTimeMs = MaxProcessingTimeMs,
                    StartTime = StartTime,
                    Uptime = Uptime,
                    SnapshotTime = DateTime.UtcNow
                };
            }
        }

        public override string ToString()
        {
            return $"Pipeline Metrics: Processed={TotalProcessed}, Success={TotalSuccessful}, " +
                   $"Failed={TotalFailed}, SuccessRate={SuccessRate:P2}, " +
                   $"AvgTime={AverageProcessingTimeMs:F2}ms, Uptime={Uptime:hh\\:mm\\:ss}";
        }
    }

    /// <summary>
    /// Immutable snapshot of pipeline metrics
    /// </summary>
    public sealed record OrderPipelineMetricsSnapshot
    {
        public long TotalProcessed { get; init; }
        public long TotalSuccessful { get; init; }
        public long TotalFailed { get; init; }
        public decimal SuccessRate { get; init; }
        public decimal FailureRate { get; init; }
        public double AverageProcessingTimeMs { get; init; }
        public long MinProcessingTimeMs { get; init; }
        public long MaxProcessingTimeMs { get; init; }
        public DateTime StartTime { get; init; }
        public TimeSpan Uptime { get; init; }
        public DateTime SnapshotTime { get; init; }
    }

    /// <summary>
    /// Metrics for individual processing stages
    /// </summary>
    public sealed class StageMetrics
    {
        private long _executionCount;
        private long _successCount;
        private long _failureCount;
        private long _totalExecutionTimeMs;
        private readonly object _lockObject = new();

        public string StageName { get; }
        public long ExecutionCount => _executionCount;
        public long SuccessCount => _successCount;
        public long FailureCount => _failureCount;
        public decimal SuccessRate => _executionCount == 0 ? 0 : (decimal)_successCount / _executionCount;
        public double AverageExecutionTimeMs => _executionCount == 0 ? 0 : (double)_totalExecutionTimeMs / _executionCount;

        public StageMetrics(string stageName)
        {
            StageName = stageName ?? throw new ArgumentNullException(nameof(stageName));
        }

        public void RecordExecution(bool success, TimeSpan executionTime)
        {
            var executionTimeMs = (long)executionTime.TotalMilliseconds;
            
            Interlocked.Increment(ref _executionCount);
            Interlocked.Add(ref _totalExecutionTimeMs, executionTimeMs);
            
            if (success)
                Interlocked.Increment(ref _successCount);
            else
                Interlocked.Increment(ref _failureCount);
        }

        public void Reset()
        {
            lock (_lockObject)
            {
                _executionCount = 0;
                _successCount = 0;
                _failureCount = 0;
                _totalExecutionTimeMs = 0;
            }
        }

        public override string ToString()
        {
            return $"Stage '{StageName}': Executions={ExecutionCount}, Success={SuccessCount}, " +
                   $"Failed={FailureCount}, SuccessRate={SuccessRate:P2}, " +
                   $"AvgTime={AverageExecutionTimeMs:F2}ms";
        }
    }
}