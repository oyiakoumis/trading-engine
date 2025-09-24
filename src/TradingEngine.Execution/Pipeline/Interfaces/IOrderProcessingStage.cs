using TradingEngine.Execution.Pipeline.Models;

namespace TradingEngine.Execution.Pipeline.Interfaces
{
    /// <summary>
    /// Individual processing stage with single responsibility
    /// Immutable context passing for thread safety
    /// </summary>
    public interface IOrderProcessingStage
    {
        /// <summary>
        /// Name of the processing stage
        /// </summary>
        string StageName { get; }
        
        /// <summary>
        /// Priority order for stage execution (lower number = higher priority)
        /// </summary>
        int Priority { get; }
        
        /// <summary>
        /// Process the order context through this stage
        /// </summary>
        ValueTask<StageResult> ProcessAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Check if this stage can process the given context
        /// </summary>
        ValueTask<bool> CanProcessAsync(OrderProcessingContext context);
        
        /// <summary>
        /// Get performance metrics for this stage
        /// </summary>
        StageMetrics GetMetrics();
        
        /// <summary>
        /// Perform health check for this stage
        /// </summary>
        ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Event raised when stage processing completes
        /// </summary>
        event EventHandler<StageCompletedEventArgs>? StageCompleted;
    }

    /// <summary>
    /// Base abstract implementation of order processing stage
    /// Provides common functionality and metrics collection
    /// </summary>
    public abstract class OrderProcessingStageBase : IOrderProcessingStage
    {
        private readonly StageMetrics _metrics;
        
        public abstract string StageName { get; }
        public abstract int Priority { get; }
        
        public event EventHandler<StageCompletedEventArgs>? StageCompleted;

        protected OrderProcessingStageBase()
        {
            _metrics = new StageMetrics(StageName);
        }

        public async ValueTask<StageResult> ProcessAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                var result = await ProcessInternalAsync(context, cancellationToken);
                var executionTime = DateTime.UtcNow - startTime;
                
                _metrics.RecordExecution(result.IsSuccess, executionTime);
                
                // Raise completion event
                StageCompleted?.Invoke(this, new StageCompletedEventArgs(StageName, result, context));
                
                return result;
            }
            catch (Exception ex)
            {
                var executionTime = DateTime.UtcNow - startTime;
                _metrics.RecordExecution(false, executionTime);
                
                var errorResult = StageResult.Failed($"Stage '{StageName}' failed: {ex.Message}", executionTime);
                StageCompleted?.Invoke(this, new StageCompletedEventArgs(StageName, errorResult, context));
                
                return errorResult;
            }
        }

        public virtual ValueTask<bool> CanProcessAsync(OrderProcessingContext context)
        {
            return ValueTask.FromResult(true);
        }

        public StageMetrics GetMetrics() => _metrics;

        public virtual ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(HealthCheckResult.Healthy($"Stage '{StageName}' is healthy"));
        }

        /// <summary>
        /// Override this method to implement stage-specific processing logic
        /// </summary>
        protected abstract ValueTask<StageResult> ProcessInternalAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken);
    }
}