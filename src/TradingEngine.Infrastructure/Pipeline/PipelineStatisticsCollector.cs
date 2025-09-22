using TradingEngine.Infrastructure.EventBus;

namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Collects and manages pipeline statistics
    /// </summary>
    public class PipelineStatisticsCollector
    {
        private readonly object _statsLock = new();
        private long _ticksProcessed;
        private long _signalsGenerated;
        private long _ordersExecuted;
        private DateTime _startTime;
        private bool _isRunning;

        public void Start()
        {
            lock (_statsLock)
            {
                _startTime = DateTime.UtcNow;
                _isRunning = true;
            }
        }

        public void Stop()
        {
            lock (_statsLock)
            {
                _isRunning = false;
            }
        }

        public void IncrementTicksProcessed()
        {
            Interlocked.Increment(ref _ticksProcessed);
        }

        public void IncrementSignalsGenerated()
        {
            Interlocked.Increment(ref _signalsGenerated);
        }

        public void IncrementOrdersExecuted()
        {
            Interlocked.Increment(ref _ordersExecuted);
        }

        public PipelineStatistics GetStatistics(int activePositions, EventBusStatistics? eventBusStats)
        {
            lock (_statsLock)
            {
                var uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero;

                return new PipelineStatistics
                {
                    IsRunning = _isRunning,
                    Uptime = uptime,
                    TicksProcessed = _ticksProcessed,
                    SignalsGenerated = _signalsGenerated,
                    OrdersExecuted = _ordersExecuted,
                    ActivePositions = activePositions,
                    EventBusStats = eventBusStats
                };
            }
        }
    }
}