using TradingEngine.Strategies.Models;
using TradingEngine.Execution.Pipeline.Models;

namespace TradingEngine.Execution.Pipeline.Interfaces
{
    /// <summary>
    /// High-performance order processing pipeline with pluggable stages
    /// Uses ValueTask for optimized async performance and minimal allocations
    /// </summary>
    public interface IOrderProcessingPipeline : IDisposable
    {
        /// <summary>
        /// Process a trading signal through the complete pipeline
        /// </summary>
        ValueTask<OrderProcessingResult> ProcessSignalAsync(
            Signal signal, 
            OrderProcessingContext context,
            CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Add a processing stage to the pipeline
        /// </summary>
        IOrderProcessingPipeline AddStage<TStage>() where TStage : class, IOrderProcessingStage;
        
        /// <summary>
        /// Remove a processing stage from the pipeline
        /// </summary>
        IOrderProcessingPipeline RemoveStage<TStage>() where TStage : class, IOrderProcessingStage;
        
        /// <summary>
        /// Get pipeline performance metrics
        /// </summary>
        OrderPipelineMetrics GetMetrics();
        
        /// <summary>
        /// Perform health check on all pipeline stages
        /// </summary>
        Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Start the pipeline processing
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Stop the pipeline processing
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Event raised when pipeline processing completes
        /// </summary>
        event EventHandler<OrderProcessingCompletedEventArgs>? ProcessingCompleted;
        
        /// <summary>
        /// Event raised when pipeline encounters an error
        /// </summary>
        event EventHandler<OrderProcessingErrorEventArgs>? ProcessingError;
    }
}