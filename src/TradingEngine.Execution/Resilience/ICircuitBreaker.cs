namespace TradingEngine.Execution.Resilience
{
    /// <summary>
    /// Circuit breaker for fault tolerance
    /// Prevents cascading failures in distributed systems
    /// </summary>
    public interface ICircuitBreaker
    {
        /// <summary>
        /// Execute operation through circuit breaker
        /// </summary>
        ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation);
        
        /// <summary>
        /// Execute operation through circuit breaker (void return)
        /// </summary>
        ValueTask ExecuteAsync(Func<ValueTask> operation);
        
        /// <summary>
        /// Current state of the circuit breaker
        /// </summary>
        CircuitBreakerState State { get; }
        
        /// <summary>
        /// Get circuit breaker metrics
        /// </summary>
        CircuitBreakerMetrics GetMetrics();
        
        /// <summary>
        /// Event raised when circuit breaker state changes
        /// </summary>
        event EventHandler<CircuitBreakerStateChangedEventArgs>? StateChanged;
    }

    /// <summary>
    /// Circuit breaker states
    /// </summary>
    public enum CircuitBreakerState
    {
        /// <summary>
        /// Normal operation - requests pass through
        /// </summary>
        Closed,
        
        /// <summary>
        /// Failing fast - requests are immediately rejected
        /// </summary>
        Open,
        
        /// <summary>
        /// Testing recovery - limited requests are allowed through
        /// </summary>
        HalfOpen
    }

    /// <summary>
    /// Circuit breaker metrics
    /// </summary>
    public sealed record CircuitBreakerMetrics
    {
        public long SuccessCount { get; init; }
        public long FailureCount { get; init; }
        public long TimeoutCount { get; init; }
        public long RejectedCount { get; init; }
        public CircuitBreakerState State { get; init; }
        public DateTime? LastFailureTime { get; init; }
        public DateTime? LastSuccessTime { get; init; }
        public TimeSpan? LastStateChangeTime { get; init; }
        public decimal FailureRate => (SuccessCount + FailureCount) > 0 
            ? (decimal)FailureCount / (SuccessCount + FailureCount) 
            : 0;
    }

    /// <summary>
    /// Circuit breaker state change event args
    /// </summary>
    public sealed class CircuitBreakerStateChangedEventArgs : EventArgs
    {
        public CircuitBreakerState PreviousState { get; }
        public CircuitBreakerState NewState { get; }
        public string? Reason { get; }
        public DateTime Timestamp { get; }

        public CircuitBreakerStateChangedEventArgs(
            CircuitBreakerState previousState,
            CircuitBreakerState newState,
            string? reason = null)
        {
            PreviousState = previousState;
            NewState = newState;
            Reason = reason;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Exception thrown when circuit breaker is open
    /// </summary>
    public sealed class CircuitBreakerOpenException : Exception
    {
        public CircuitBreakerOpenException() 
            : base("Circuit breaker is open")
        {
        }

        public CircuitBreakerOpenException(string message) 
            : base(message)
        {
        }

        public CircuitBreakerOpenException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Circuit breaker configuration options
    /// </summary>
    public sealed record CircuitBreakerOptions
    {
        /// <summary>
        /// Number of failures required to open circuit
        /// </summary>
        public int FailureThreshold { get; init; } = 5;
        
        /// <summary>
        /// Time to wait before transitioning from Open to HalfOpen
        /// </summary>
        public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromSeconds(30);
        
        /// <summary>
        /// Number of successful calls required to close circuit from HalfOpen
        /// </summary>
        public int SuccessThreshold { get; init; } = 3;
        
        /// <summary>
        /// Timeout for individual operations
        /// </summary>
        public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(10);
        
        /// <summary>
        /// Minimum number of calls before circuit breaker can open
        /// </summary>
        public int MinimumThroughput { get; init; } = 10;
        
        /// <summary>
        /// Time window for failure rate calculation
        /// </summary>
        public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromMinutes(1);
    }
}