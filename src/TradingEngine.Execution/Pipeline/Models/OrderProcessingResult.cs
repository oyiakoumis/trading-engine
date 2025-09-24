using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Execution.Pipeline.Models
{
    /// <summary>
    /// Result of order processing pipeline execution
    /// Immutable result with comprehensive error information
    /// </summary>
    public sealed record OrderProcessingResult
    {
        public bool IsSuccess { get; init; }
        public OrderId? OrderId { get; init; }
        public string? ErrorMessage { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public TimeSpan ProcessingTime { get; init; }
        public RiskLevel RiskLevel { get; init; }
        public string? CorrelationId { get; init; }
        public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
            new Dictionary<string, object>();

        public static OrderProcessingResult Success(
            OrderId orderId,
            TimeSpan processingTime,
            RiskLevel riskLevel = RiskLevel.Low,
            string? correlationId = null,
            IReadOnlyDictionary<string, object>? metadata = null)
        {
            return new OrderProcessingResult
            {
                IsSuccess = true,
                OrderId = orderId,
                ProcessingTime = processingTime,
                RiskLevel = riskLevel,
                CorrelationId = correlationId,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
        }

        public static OrderProcessingResult Failure(
            string errorMessage,
            TimeSpan processingTime,
            IReadOnlyList<string>? errors = null,
            string? correlationId = null,
            IReadOnlyDictionary<string, object>? metadata = null)
        {
            return new OrderProcessingResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                Errors = errors ?? Array.Empty<string>(),
                ProcessingTime = processingTime,
                CorrelationId = correlationId,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
        }

        public static OrderProcessingResult Failure(
            IReadOnlyList<string> errors,
            TimeSpan processingTime,
            string? correlationId = null,
            IReadOnlyDictionary<string, object>? metadata = null)
        {
            var primaryError = errors.Count > 0 ? errors[0] : "Unknown error";
            return new OrderProcessingResult
            {
                IsSuccess = false,
                ErrorMessage = primaryError,
                Errors = errors,
                ProcessingTime = processingTime,
                CorrelationId = correlationId,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
        }
    }

    /// <summary>
    /// Result of individual stage processing
    /// </summary>
    public sealed record StageResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public IReadOnlyDictionary<string, object> Data { get; init; } = 
            new Dictionary<string, object>();
        public TimeSpan ExecutionTime { get; init; }

        public static StageResult Success(
            TimeSpan executionTime = default,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new StageResult
            {
                IsSuccess = true,
                ExecutionTime = executionTime,
                Data = data ?? new Dictionary<string, object>()
            };
        }

        public static StageResult Failed(
            string errorMessage,
            TimeSpan executionTime = default,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new StageResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ExecutionTime = executionTime,
                Data = data ?? new Dictionary<string, object>()
            };
        }
    }

    /// <summary>
    /// Health check result for pipeline components
    /// </summary>
    public sealed record HealthCheckResult
    {
        public bool IsHealthy { get; init; }
        public string? Description { get; init; }
        public IReadOnlyDictionary<string, object> Data { get; init; } = 
            new Dictionary<string, object>();
        public TimeSpan ResponseTime { get; init; }
        public DateTime CheckedAt { get; init; } = DateTime.UtcNow;

        public static HealthCheckResult Healthy(
            string? description = null,
            TimeSpan responseTime = default,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new HealthCheckResult
            {
                IsHealthy = true,
                Description = description ?? "Healthy",
                ResponseTime = responseTime,
                Data = data ?? new Dictionary<string, object>()
            };
        }

        public static HealthCheckResult Unhealthy(
            string description,
            TimeSpan responseTime = default,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new HealthCheckResult
            {
                IsHealthy = false,
                Description = description,
                ResponseTime = responseTime,
                Data = data ?? new Dictionary<string, object>()
            };
        }
    }
}