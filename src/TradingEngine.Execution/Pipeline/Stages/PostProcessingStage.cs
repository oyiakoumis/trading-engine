using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Execution.Pipeline.Stages
{
    /// <summary>
    /// Final processing stage with metrics, logging, and event publishing
    /// Non-blocking operations for optimal performance
    /// Fire-and-forget pattern for non-critical operations
    /// </summary>
    public sealed class PostProcessingStage : OrderProcessingStageBase
    {
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metrics;
        private readonly ILogger<PostProcessingStage>? _logger;

        public override string StageName => "PostProcessing";
        public override int Priority => 500; // Final stage

        public PostProcessingStage(
            IEventBus eventBus,
            IMetricsCollector metrics,
            ILogger<PostProcessingStage>? logger = null)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _logger = logger;
        }

        protected override async ValueTask<StageResult> ProcessInternalAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken)
        {
            var processingTime = context.GetProcessingTime();
            var postProcessingData = new Dictionary<string, object>();

            try
            {
                // Fire-and-forget metrics collection (non-blocking)
                _ = Task.Run(() => CollectMetricsAsync(context, processingTime), cancellationToken);

                // Publish order processing completed event
                var orderProcessedEvent = CreateOrderProcessedEvent(context, processingTime);
                await _eventBus.PublishAsync(orderProcessedEvent, cancellationToken);

                // Publish domain events for external systems
                if (context.OrderId.HasValue)
                {
                    var orderCreatedEvent = new OrderCreatedEvent
                    {
                        OrderId = context.OrderId.Value,
                        Symbol = context.Signal.Symbol,
                        Side = context.Signal.Side,
                        Quantity = context.Signal.Quantity,
                        OrderType = GetOrderTypeFromContext(context),
                        CreatedAt = Timestamp.Now,
                        CorrelationId = context.CorrelationId
                    };

                    // Non-blocking event publishing
                    _ = Task.Run(() => _eventBus.PublishAsync(orderCreatedEvent, cancellationToken), 
                        cancellationToken);
                }

                // Structured logging for observability
                LogOrderProcessingCompletion(context, processingTime);

                // Optional: Update real-time dashboards (fire-and-forget)
                _ = Task.Run(() => UpdateDashboardsAsync(context, processingTime), cancellationToken);

                postProcessingData.Add("ProcessingTimeMs", processingTime.TotalMilliseconds);
                postProcessingData.Add("EventsPublished", 2);
                postProcessingData.Add("MetricsCollected", true);

                return StageResult.Success(data: postProcessingData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Error in post-processing for order {OrderId}, correlation {CorrelationId}",
                    context.OrderId,
                    context.CorrelationId);

                // Post-processing errors should not fail the entire pipeline
                // Log the error but return success
                postProcessingData.Add("ProcessingTimeMs", processingTime.TotalMilliseconds);
                postProcessingData.Add("PostProcessingError", ex.Message);

                return StageResult.Success(data: postProcessingData);
            }
        }

        private async Task CollectMetricsAsync(OrderProcessingContext context, TimeSpan processingTime)
        {
            try
            {
                // Record processing latency
                await _metrics.RecordProcessingLatencyAsync(
                    context.Signal.Symbol,
                    processingTime);

                // Record order metrics
                await _metrics.IncrementOrdersProcessedAsync(context.Signal.Symbol);

                // Record risk level metrics
                await _metrics.RecordRiskLevelAsync(
                    context.Signal.Symbol,
                    context.RiskLevel);

                // Record signal confidence metrics
                await _metrics.RecordSignalConfidenceAsync(
                    context.Signal.Symbol,
                    context.Signal.Confidence);

                _logger?.LogDebug(
                    "Metrics collected for order {OrderId}",
                    context.OrderId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error collecting metrics for order {OrderId}", context.OrderId);
            }
        }

        private OrderProcessedEvent CreateOrderProcessedEvent(OrderProcessingContext context, TimeSpan processingTime)
        {
            return new OrderProcessedEvent
            {
                OrderId = context.OrderId,
                Signal = context.Signal,
                ProcessingTime = processingTime,
                RiskLevel = context.RiskLevel,
                CorrelationId = context.CorrelationId,
                ProcessedAt = Timestamp.Now,
                StageCount = 5, // Number of stages processed
                Success = !context.HasErrors
            };
        }

        private string GetOrderTypeFromContext(OrderProcessingContext context)
        {
            // Try to get order type from context properties
            var orderType = context.GetProperty<string>("OrderType");
            if (!string.IsNullOrEmpty(orderType))
                return orderType;

            // Fallback to signal-based determination
            return context.Signal.TargetPrice.HasValue ? "Limit" : "Market";
        }

        private void LogOrderProcessingCompletion(OrderProcessingContext context, TimeSpan processingTime)
        {
            try
            {
                if (context.HasErrors)
                {
                    _logger?.LogWarning(
                        "Order processing completed with errors for {Symbol} {Side} {Quantity}. " +
                        "OrderId: {OrderId}, ProcessingTime: {ProcessingTime}ms, " +
                        "CorrelationId: {CorrelationId}, Errors: {Errors}",
                        context.Signal.Symbol,
                        context.Signal.Side,
                        context.Signal.Quantity,
                        context.OrderId,
                        processingTime.TotalMilliseconds,
                        context.CorrelationId,
                        context.GetErrorSummary());
                }
                else
                {
                    _logger?.LogInformation(
                        "Order processing completed successfully for {Symbol} {Side} {Quantity}. " +
                        "OrderId: {OrderId}, ProcessingTime: {ProcessingTime}ms, " +
                        "RiskLevel: {RiskLevel}, Confidence: {Confidence:P2}, " +
                        "CorrelationId: {CorrelationId}",
                        context.Signal.Symbol,
                        context.Signal.Side,
                        context.Signal.Quantity,
                        context.OrderId,
                        processingTime.TotalMilliseconds,
                        context.RiskLevel,
                        context.Signal.Confidence,
                        context.CorrelationId);
                }
            }
            catch (Exception ex)
            {
                // Even logging failures should not break post-processing
                try
                {
                    _logger?.LogError(ex, "Error logging order processing completion");
                }
                catch
                {
                    // Ultimate fallback - silently continue
                }
            }
        }

        private async Task UpdateDashboardsAsync(OrderProcessingContext context, TimeSpan processingTime)
        {
            try
            {
                // This would update real-time trading dashboards
                // Implementation would depend on dashboard technology (SignalR, WebSockets, etc.)
                await Task.Delay(1); // Placeholder for actual dashboard update logic
                
                _logger?.LogDebug(
                    "Dashboard updated for order {OrderId}",
                    context.OrderId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating dashboards for order {OrderId}", context.OrderId);
            }
        }

        public override ValueTask<bool> CanProcessAsync(OrderProcessingContext context)
        {
            // PostProcessingStage can process any context
            return ValueTask.FromResult(context?.Signal != null);
        }

        public override async ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var healthData = new Dictionary<string, object>();

                // Check event bus health
                var eventBusStats = _eventBus.GetStatistics();
                var eventBusHealthy = eventBusStats.TotalEventsFailed < eventBusStats.TotalEventsPublished * 0.05m; // Less than 5% failure rate

                // Check metrics collector health
                var metricsHealthy = await _metrics.IsHealthyAsync();

                var responseTime = DateTime.UtcNow - startTime;
                var metrics = GetMetrics();

                healthData.Add("EventBusHealthy", eventBusHealthy);
                healthData.Add("EventBusStats", new
                {
                    eventBusStats.TotalEventsPublished,
                    eventBusStats.TotalEventsFailed,
                    eventBusStats.ActiveSubscriptions
                });
                healthData.Add("MetricsCollectorHealthy", metricsHealthy);
                healthData.Add("ExecutionCount", metrics.ExecutionCount);
                healthData.Add("SuccessRate", metrics.SuccessRate);
                healthData.Add("AverageExecutionTime", metrics.AverageExecutionTimeMs);

                var isHealthy = eventBusHealthy && metricsHealthy;

                if (!isHealthy)
                {
                    var issues = new List<string>();
                    if (!eventBusHealthy) issues.Add("Event bus unhealthy");
                    if (!metricsHealthy) issues.Add("Metrics collector unhealthy");

                    return HealthCheckResult.Unhealthy(
                        $"Post-processing issues: {string.Join(", ", issues)}",
                        responseTime,
                        healthData);
                }

                return HealthCheckResult.Healthy(
                    "Post-processing stage is healthy",
                    responseTime,
                    healthData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Health check failed for PostProcessingStage");

                return HealthCheckResult.Unhealthy(
                    $"Health check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Interface for metrics collection
    /// </summary>
    public interface IMetricsCollector
    {
        ValueTask RecordProcessingLatencyAsync(Symbol symbol, TimeSpan latency);
        ValueTask IncrementOrdersProcessedAsync(Symbol symbol);
        ValueTask RecordRiskLevelAsync(Symbol symbol, RiskLevel riskLevel);
        ValueTask RecordSignalConfidenceAsync(Symbol symbol, double confidence);
        ValueTask<bool> IsHealthyAsync();
    }

    /// <summary>
    /// Local event bus interface for pipeline events
    /// </summary>
    public interface IEventBus
    {
        ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IEvent;
        
        /// <summary>
        /// Get event bus statistics
        /// </summary>
        EventBusStatistics GetStatistics();
    }
    
    /// <summary>
    /// Event bus statistics
    /// </summary>
    public sealed class EventBusStatistics
    {
        public long TotalEventsPublished { get; set; }
        public long TotalEventsFailed { get; set; }
        public int ActiveSubscriptions { get; set; }
    }

    /// <summary>
    /// Event published when order processing is completed
    /// </summary>
    public sealed class OrderProcessedEvent : IEvent
    {
        public string EventId { get; } = Guid.NewGuid().ToString("N");
        public string EventType { get; } = nameof(OrderProcessedEvent);
        public Timestamp Timestamp { get; } = Timestamp.Now;
        
        public OrderId? OrderId { get; set; }
        public Strategies.Models.Signal Signal { get; set; } = null!;
        public TimeSpan ProcessingTime { get; set; }
        public RiskLevel RiskLevel { get; set; }
        public string? CorrelationId { get; set; }
        public Timestamp ProcessedAt { get; set; }
        public int StageCount { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Event published when order is created
    /// </summary>
    public sealed class OrderCreatedEvent : IEvent
    {
        public string EventId { get; } = Guid.NewGuid().ToString("N");
        public string EventType { get; } = nameof(OrderCreatedEvent);
        public Timestamp Timestamp { get; } = Timestamp.Now;
        
        public OrderId OrderId { get; set; }
        public Symbol Symbol { get; set; }
        public Domain.Enums.OrderSide Side { get; set; }
        public Quantity Quantity { get; set; }
        public string OrderType { get; set; } = string.Empty;
        public Timestamp CreatedAt { get; set; }
        public string? CorrelationId { get; set; }
    }
}