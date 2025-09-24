using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Execution.Resilience;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Execution.Pipeline.Stages
{
    /// <summary>
    /// Pre-trade risk assessment stage with circuit breaker integration
    /// Integrates with risk management system for comprehensive risk checks
    /// </summary>
    public sealed class RiskAssessmentStage : OrderProcessingStageBase
    {
        private readonly IRiskAssessment _riskAssessment;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly ILogger<RiskAssessmentStage>? _logger;
        
        public override string StageName => "RiskAssessment";
        public override int Priority => 200; // Second stage after validation

        public RiskAssessmentStage(
            IRiskAssessment riskAssessment,
            ICircuitBreaker circuitBreaker,
            ILogger<RiskAssessmentStage>? logger = null)
        {
            _riskAssessment = riskAssessment ?? throw new ArgumentNullException(nameof(riskAssessment));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _logger = logger;
        }

        protected override async ValueTask<StageResult> ProcessInternalAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                // Create a mock order for risk assessment
                var mockOrder = CreateMockOrderFromSignal(context.Signal);

                // Perform risk check through circuit breaker
                var riskResult = await _circuitBreaker.ExecuteAsync(async () =>
                {
                    return await _riskAssessment.CheckPreTradeRiskAsync(mockOrder, cancellationToken);
                });

                if (!riskResult.Passed)
                {
                    _logger?.LogWarning(
                        "Risk assessment failed for signal {CorrelationId}: {Reason}",
                        context.CorrelationId,
                        riskResult.RejectionReason);

                    return StageResult.Failed($"Risk check failed: {riskResult.RejectionReason}");
                }

                // Add risk information to context
                var contextData = new Dictionary<string, object>
                {
                    ["RiskLevel"] = riskResult.RiskLevel.ToString(),
                    ["RiskDetails"] = riskResult.Details
                };

                _logger?.LogDebug(
                    "Risk assessment passed for signal {CorrelationId} with risk level {RiskLevel}",
                    context.CorrelationId,
                    riskResult.RiskLevel);

                return StageResult.Success(data: contextData);
            }
            catch (CircuitBreakerOpenException)
            {
                _logger?.LogError(
                    "Risk assessment circuit breaker is open for signal {CorrelationId}",
                    context.CorrelationId);

                return StageResult.Failed("Risk assessment service is currently unavailable");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Unexpected error during risk assessment for signal {CorrelationId}",
                    context.CorrelationId);

                return StageResult.Failed($"Risk assessment error: {ex.Message}");
            }
        }

        private Order CreateMockOrderFromSignal(Strategies.Models.Signal signal)
        {
            // Determine order type based on signal
            var orderType = signal.TargetPrice.HasValue ? OrderType.Limit : OrderType.Market;

            // Create order for risk assessment (won't be persisted)
            return new Order(
                signal.Symbol,
                signal.Side,
                orderType,
                signal.Quantity,
                signal.TargetPrice,
                signal.StopLoss,
                $"Risk_Assessment_{signal.GeneratedAt.UnixMilliseconds}",
                "RiskCheck");
        }

        public override async ValueTask<bool> CanProcessAsync(OrderProcessingContext context)
        {
            // Only process if we have a valid signal and risk assessment is available
            if (context?.Signal == null)
                return false;

            // Check if risk assessment service is available
            return await _riskAssessment.IsAvailableAsync();
        }

        public override async ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                
                // Check risk assessment availability
                var isAvailable = await _riskAssessment.IsAvailableAsync();
                var circuitBreakerState = _circuitBreaker.State;
                
                var responseTime = DateTime.UtcNow - startTime;
                var metrics = GetMetrics();

                var healthData = new Dictionary<string, object>
                {
                    ["RiskAssessmentAvailable"] = isAvailable,
                    ["CircuitBreakerState"] = circuitBreakerState.ToString(),
                    ["ExecutionCount"] = metrics.ExecutionCount,
                    ["SuccessRate"] = metrics.SuccessRate,
                    ["AverageExecutionTime"] = metrics.AverageExecutionTimeMs
                };

                if (!isAvailable || circuitBreakerState == CircuitBreakerState.Open)
                {
                    return HealthCheckResult.Unhealthy(
                        "Risk assessment service is unavailable or circuit breaker is open",
                        responseTime,
                        healthData);
                }

                return HealthCheckResult.Healthy(
                    "Risk assessment stage is healthy",
                    responseTime,
                    healthData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Health check failed for RiskAssessmentStage");
                
                return HealthCheckResult.Unhealthy(
                    $"Health check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Interface for risk assessment operations
    /// Abstraction layer for risk management integration
    /// </summary>
    public interface IRiskAssessment
    {
        /// <summary>
        /// Perform pre-trade risk check
        /// </summary>
        ValueTask<RiskCheckResult> CheckPreTradeRiskAsync(
            Order order, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Check if risk assessment service is available
        /// </summary>
        ValueTask<bool> IsAvailableAsync();

        /// <summary>
        /// Get current risk limits
        /// </summary>
        ValueTask<RiskLimits> GetRiskLimitsAsync();
    }

    /// <summary>
    /// Result of risk assessment
    /// </summary>
    public sealed record RiskCheckResult
    {
        public bool Passed { get; init; }
        public string? RejectionReason { get; init; }
        public RiskLevel RiskLevel { get; init; }
        public IReadOnlyDictionary<string, object> Details { get; init; } = 
            new Dictionary<string, object>();

        public static RiskCheckResult Pass(
            RiskLevel level = RiskLevel.Low,
            IReadOnlyDictionary<string, object>? details = null)
        {
            return new RiskCheckResult
            {
                Passed = true,
                RiskLevel = level,
                Details = details ?? new Dictionary<string, object>()
            };
        }

        public static RiskCheckResult Fail(
            string reason,
            RiskLevel level = RiskLevel.High,
            IReadOnlyDictionary<string, object>? details = null)
        {
            return new RiskCheckResult
            {
                Passed = false,
                RejectionReason = reason,
                RiskLevel = level,
                Details = details ?? new Dictionary<string, object>()
            };
        }
    }

    /// <summary>
    /// Risk limits configuration
    /// </summary>
    public sealed record RiskLimits
    {
        public decimal MaxPositionSize { get; init; } = 10000;
        public decimal MaxOrderValue { get; init; } = 100000;
        public decimal MaxDailyLoss { get; init; } = 10000;
        public decimal MaxDrawdown { get; init; } = 0.20m;
        public decimal MaxExposure { get; init; } = 500000;
        public decimal MaxLeverage { get; init; } = 2.0m;
        public int MaxOpenPositions { get; init; } = 20;
        public int MaxOrdersPerMinute { get; init; } = 100;
        public decimal ConcentrationLimit { get; init; } = 0.30m;
    }
}