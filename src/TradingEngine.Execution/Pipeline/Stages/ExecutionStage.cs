using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Execution.Resilience;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Execution.Pipeline.Stages
{
    /// <summary>
    /// Exchange routing stage with resilience patterns
    /// Circuit breaker, rate limiting, and bulkhead isolation for exchange operations
    /// </summary>
    public sealed class ExecutionStage : OrderProcessingStageBase
    {
        private readonly IExchange _exchange;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly IRateLimiter _rateLimiter;
        private readonly IBulkheadIsolation _bulkhead;
        private readonly IOrderTracker _orderTracker;
        private readonly ILogger<ExecutionStage>? _logger;

        public override string StageName => "Execution";
        public override int Priority => 400; // Fourth stage after creation

        public ExecutionStage(
            IExchange exchange,
            ICircuitBreaker circuitBreaker,
            IRateLimiter rateLimiter,
            IBulkheadIsolation bulkhead,
            IOrderTracker orderTracker,
            ILogger<ExecutionStage>? logger = null)
        {
            _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _bulkhead = bulkhead ?? throw new ArgumentNullException(nameof(bulkhead));
            _orderTracker = orderTracker ?? throw new ArgumentNullException(nameof(orderTracker));
            _logger = logger;
        }

        protected override async ValueTask<StageResult> ProcessInternalAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken)
        {
            if (context.OrderId == null)
            {
                return StageResult.Failed("No order ID found in context - order must be created first");
            }

            try
            {
                // Get the order from the tracker
                var order = await _orderTracker.GetOrderAsync(context.OrderId.Value, cancellationToken);
                if (order == null)
                {
                    return StageResult.Failed($"Order {context.OrderId} not found in order tracker");
                }

                // Apply rate limiting to prevent exchange overload
                await _rateLimiter.WaitAsync(cancellationToken);

                _logger?.LogDebug(
                    "Submitting order {OrderId} to exchange for signal {CorrelationId}",
                    context.OrderId,
                    context.CorrelationId);

                // Execute submission through bulkhead isolation
                var executionResult = await _bulkhead.ExecuteAsync(async () =>
                {
                    // Submit through circuit breaker for exchange connectivity
                    return await _circuitBreaker.ExecuteAsync(async () =>
                    {
                        return await _exchange.SubmitOrderAsync(order);
                    });
                });

                if (!executionResult)
                {
                    _logger?.LogWarning(
                        "Exchange rejected order {OrderId} for signal {CorrelationId}",
                        context.OrderId,
                        context.CorrelationId);

                    return StageResult.Failed("Exchange submission failed");
                }

                _logger?.LogInformation(
                    "Order {OrderId} successfully submitted to exchange for signal {CorrelationId}",
                    context.OrderId,
                    context.CorrelationId);

                // Store execution information
                var executionData = new Dictionary<string, object>
                {
                    ["SubmittedAt"] = DateTime.UtcNow,
                    ["Exchange"] = _exchange.GetType().Name,
                    ["CircuitBreakerState"] = _circuitBreaker.State.ToString()
                };

                return StageResult.Success(data: executionData);
            }
            catch (CircuitBreakerOpenException)
            {
                _logger?.LogError(
                    "Exchange circuit breaker is open - cannot submit order {OrderId}",
                    context.OrderId);

                return StageResult.Failed("Exchange service is currently unavailable (circuit breaker open)");
            }
            catch (RateLimitExceededException ex)
            {
                _logger?.LogWarning(
                    "Rate limit exceeded for order {OrderId}: {Message}",
                    context.OrderId,
                    ex.Message);

                return StageResult.Failed($"Rate limit exceeded: {ex.Message}");
            }
            catch (BulkheadRejectionException ex)
            {
                _logger?.LogWarning(
                    "Bulkhead isolation rejected order {OrderId}: {Message}",
                    context.OrderId,
                    ex.Message);

                return StageResult.Failed($"Service overloaded: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Unexpected error during order execution for order {OrderId}",
                    context.OrderId);

                return StageResult.Failed($"Execution error: {ex.Message}");
            }
        }

        public override async ValueTask<bool> CanProcessAsync(OrderProcessingContext context)
        {
            // Can only process if we have an order ID and the order exists
            if (context?.OrderId == null)
                return false;

            try
            {
                var order = await _orderTracker.GetOrderAsync(context.OrderId.Value);
                return order != null && order.IsActive;
            }
            catch
            {
                return false;
            }
        }

        public override async ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var healthData = new Dictionary<string, object>();

                // Check exchange health
                var exchangeHealthy = true;
                try
                {
                    // This would be a ping or health check to exchange
                    // For now, just check if it's not null
                    exchangeHealthy = _exchange != null;
                }
                catch (Exception ex)
                {
                    exchangeHealthy = false;
                    healthData["ExchangeError"] = ex.Message;
                }

                // Check circuit breaker state
                var circuitBreakerMetrics = _circuitBreaker.GetMetrics();
                var rateLimiterHealth = await _rateLimiter.GetHealthAsync();
                var bulkheadHealth = _bulkhead.GetHealth();

                var responseTime = DateTime.UtcNow - startTime;
                var metrics = GetMetrics();

                healthData.Add("ExchangeHealthy", exchangeHealthy);
                healthData.Add("CircuitBreakerState", circuitBreakerMetrics.State.ToString());
                healthData.Add("CircuitBreakerFailureRate", circuitBreakerMetrics.FailureRate);
                healthData.Add("RateLimiterHealthy", rateLimiterHealth.IsHealthy);
                healthData.Add("BulkheadHealthy", bulkheadHealth.IsHealthy);
                healthData.Add("ExecutionCount", metrics.ExecutionCount);
                healthData.Add("SuccessRate", metrics.SuccessRate);
                healthData.Add("AverageExecutionTime", metrics.AverageExecutionTimeMs);

                var isHealthy = exchangeHealthy &&
                               circuitBreakerMetrics.State != CircuitBreakerState.Open &&
                               rateLimiterHealth.IsHealthy &&
                               bulkheadHealth.IsHealthy;

                if (!isHealthy)
                {
                    var issues = new List<string>();
                    if (!exchangeHealthy) issues.Add("Exchange unhealthy");
                    if (circuitBreakerMetrics.State == CircuitBreakerState.Open) issues.Add("Circuit breaker open");
                    if (!rateLimiterHealth.IsHealthy) issues.Add("Rate limiter unhealthy");
                    if (!bulkheadHealth.IsHealthy) issues.Add("Bulkhead unhealthy");

                    return HealthCheckResult.Unhealthy(
                        $"Execution stage issues: {string.Join(", ", issues)}",
                        responseTime,
                        healthData);
                }

                return HealthCheckResult.Healthy(
                    "Execution stage is healthy",
                    responseTime,
                    healthData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Health check failed for ExecutionStage");

                return HealthCheckResult.Unhealthy(
                    $"Health check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Interface for order tracking operations
    /// </summary>
    public interface IOrderTracker
    {
        /// <summary>
        /// Get order by ID
        /// </summary>
        ValueTask<Domain.Entities.Order?> GetOrderAsync(
            OrderId orderId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Track order creation
        /// </summary>
        ValueTask TrackOrderAsync(
            Domain.Entities.Order order,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Update order status
        /// </summary>
        ValueTask UpdateOrderAsync(
            Domain.Entities.Order order,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Interface for rate limiting
    /// </summary>
    public interface IRateLimiter
    {
        /// <summary>
        /// Wait for rate limit allowance
        /// </summary>
        ValueTask WaitAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Try to acquire rate limit allowance
        /// </summary>
        bool TryAcquire();

        /// <summary>
        /// Get rate limiter health status
        /// </summary>
        ValueTask<RateLimiterHealth> GetHealthAsync();
    }

    /// <summary>
    /// Interface for bulkhead isolation
    /// </summary>
    public interface IBulkheadIsolation
    {
        /// <summary>
        /// Execute operation through bulkhead isolation
        /// </summary>
        ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation);

        /// <summary>
        /// Get bulkhead health status
        /// </summary>
        BulkheadHealth GetHealth();
    }

    /// <summary>
    /// Rate limiter health status
    /// </summary>
    public sealed record RateLimiterHealth
    {
        public bool IsHealthy { get; init; }
        public int CurrentRequests { get; init; }
        public int MaxRequests { get; init; }
        public TimeSpan WindowSize { get; init; }
        public DateTime WindowStart { get; init; }
    }

    /// <summary>
    /// Bulkhead health status
    /// </summary>
    public sealed record BulkheadHealth
    {
        public bool IsHealthy { get; init; }
        public int ActiveExecutions { get; init; }
        public int MaxConcurrentExecutions { get; init; }
        public int QueuedRequests { get; init; }
        public int MaxQueueSize { get; init; }
    }

    /// <summary>
    /// Rate limit exceeded exception
    /// </summary>
    public sealed class RateLimitExceededException : Exception
    {
        public RateLimitExceededException() : base("Rate limit exceeded")
        {
        }

        public RateLimitExceededException(string message) : base(message)
        {
        }

        public RateLimitExceededException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Bulkhead rejection exception
    /// </summary>
    public sealed class BulkheadRejectionException : Exception
    {
        public BulkheadRejectionException() : base("Bulkhead rejected execution")
        {
        }

        public BulkheadRejectionException(string message) : base(message)
        {
        }

        public BulkheadRejectionException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }
}