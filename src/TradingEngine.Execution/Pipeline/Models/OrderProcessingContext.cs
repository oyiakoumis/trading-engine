using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Execution.Pipeline.Models
{
    /// <summary>
    /// Risk level enumeration for pipeline processing
    /// </summary>
    public enum RiskLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Immutable context for pipeline processing
    /// Uses record types for structural equality and reduced allocations
    /// Thread-safe with immutable state transitions
    /// </summary>
    public sealed record OrderProcessingContext
    {
        public Signal Signal { get; init; }
        public OrderId? OrderId { get; private set; }
        public Order? Order { get; private set; }
        public RiskLevel RiskLevel { get; private set; }
        public IReadOnlyDictionary<string, object> Properties { get; private set; } = 
            new Dictionary<string, object>();
        public Timestamp StartTime { get; init; } = Timestamp.Now;
        public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
        public string? CorrelationId { get; init; } = Guid.NewGuid().ToString("N")[..8];
        
        public OrderProcessingContext(Signal signal)
        {
            Signal = signal ?? throw new ArgumentNullException(nameof(signal));
        }
        
        /// <summary>
        /// Set the order ID created during processing
        /// </summary>
        public OrderProcessingContext SetOrderId(OrderId orderId) =>
            this with { OrderId = orderId };
        
        /// <summary>
        /// Set the order instance created during processing
        /// </summary>
        public OrderProcessingContext SetOrder(Order order) =>
            this with { Order = order };
        
        /// <summary>
        /// Set the risk level assessed during processing
        /// </summary>
        public OrderProcessingContext SetRiskLevel(RiskLevel riskLevel) =>
            this with { RiskLevel = riskLevel };
        
        /// <summary>
        /// Add a property to the context
        /// </summary>
        public OrderProcessingContext SetProperty(string key, object value)
        {
            var newProperties = new Dictionary<string, object>(Properties) { [key] = value };
            return this with { Properties = newProperties };
        }
        
        /// <summary>
        /// Add an error to the context
        /// </summary>
        public OrderProcessingContext AddError(string error)
        {
            var newErrors = new List<string>(Errors) { error };
            return this with { Errors = newErrors };
        }
        
        /// <summary>
        /// Add multiple errors to the context
        /// </summary>
        public OrderProcessingContext AddErrors(IEnumerable<string> errors)
        {
            var newErrors = new List<string>(Errors);
            newErrors.AddRange(errors);
            return this with { Errors = newErrors };
        }
        
        /// <summary>
        /// Get a property value with type safety
        /// </summary>
        public T? GetProperty<T>(string key)
        {
            return Properties.TryGetValue(key, out var value) && value is T typed ? typed : default;
        }
        
        /// <summary>
        /// Check if context has errors
        /// </summary>
        public bool HasErrors => Errors.Count > 0;
        
        /// <summary>
        /// Get total processing time elapsed
        /// </summary>
        public TimeSpan GetProcessingTime() => Timestamp.Now.Value - StartTime.Value;
        
        /// <summary>
        /// Get formatted error summary
        /// </summary>
        public string GetErrorSummary() => string.Join("; ", Errors);
    }
}