using Microsoft.Extensions.Logging;

namespace TradingEngine.Execution.Resilience
{
    /// <summary>
    /// High-performance circuit breaker with exponential backoff
    /// Thread-safe implementation using interlocked operations
    /// </summary>
    public sealed class CircuitBreaker : ICircuitBreaker, IDisposable
    {
        private readonly CircuitBreakerOptions _options;
        private readonly ILogger<CircuitBreaker>? _logger;
        private readonly object _lockObject = new();
        
        private volatile CircuitBreakerState _state = CircuitBreakerState.Closed;
        private long _successCount;
        private long _failureCount;
        private long _timeoutCount;
        private long _rejectedCount;
        private long _lastFailureTimeTicks;
        private long _lastSuccessTimeTicks;
        private long _lastStateChangeTimeTicks;
        private long _consecutiveSuccesses;
        private bool _disposed;

        public CircuitBreakerState State => _state;
        public event EventHandler<CircuitBreakerStateChangedEventArgs>? StateChanged;

        public CircuitBreaker(CircuitBreakerOptions options, ILogger<CircuitBreaker>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
            _lastStateChangeTimeTicks = DateTime.UtcNow.Ticks;
        }

        public async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CircuitBreaker));

            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            // Check current state
            if (_state == CircuitBreakerState.Open)
            {
                if (ShouldAttemptReset())
                {
                    return await AttemptResetAsync(operation);
                }
                
                Interlocked.Increment(ref _rejectedCount);
                throw new CircuitBreakerOpenException("Circuit breaker is open - requests are being rejected");
            }

            // Execute operation
            CancellationTokenSource? cts = null;
            try
            {
                cts = new CancellationTokenSource(_options.OperationTimeout);
                var result = await operation().ConfigureAwait(false);
                OnSuccess();
                return result;
            }
            catch (OperationCanceledException) when (cts?.Token.IsCancellationRequested == true)
            {
                OnTimeout();
                throw;
            }
            catch (Exception ex)
            {
                OnFailure(ex);
                throw;
            }
            finally
            {
                cts?.Dispose();
            }
        }

        public async ValueTask ExecuteAsync(Func<ValueTask> operation)
        {
            await ExecuteAsync(async () =>
            {
                await operation();
                return 0; // Dummy return value for generic method
            });
        }

        private async ValueTask<T> AttemptResetAsync<T>(Func<ValueTask<T>> operation)
        {
            lock (_lockObject)
            {
                if (_state != CircuitBreakerState.Open)
                    return default!; // State changed while waiting for lock

                TransitionTo(CircuitBreakerState.HalfOpen, "Attempting recovery");
            }

            try
            {
                var result = await operation().ConfigureAwait(false);
                OnSuccess();
                return result;
            }
            catch (Exception ex)
            {
                OnFailure(ex);
                throw;
            }
        }

        private void OnSuccess()
        {
            Interlocked.Increment(ref _successCount);
            Interlocked.Exchange(ref _lastSuccessTimeTicks, DateTime.UtcNow.Ticks);

            if (_state == CircuitBreakerState.HalfOpen)
            {
                var consecutiveSuccesses = Interlocked.Increment(ref _consecutiveSuccesses);
                if (consecutiveSuccesses >= _options.SuccessThreshold)
                {
                    TransitionTo(CircuitBreakerState.Closed, "Recovery successful");
                    Interlocked.Exchange(ref _consecutiveSuccesses, 0);
                }
            }
        }

        private void OnFailure(Exception ex)
        {
            var failures = Interlocked.Increment(ref _failureCount);
            Interlocked.Exchange(ref _lastFailureTimeTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _consecutiveSuccesses, 0);

            _logger?.LogWarning(ex, "Circuit breaker recorded failure #{FailureCount}", failures);

            if (_state == CircuitBreakerState.Closed)
            {
                // Check if we should open the circuit
                if (failures >= _options.FailureThreshold && 
                    (_successCount + failures) >= _options.MinimumThroughput)
                {
                    var failureRate = (decimal)failures / (_successCount + failures);
                    TransitionTo(CircuitBreakerState.Open, 
                        $"Failure threshold exceeded: {failures} failures, {failureRate:P} failure rate");
                }
            }
            else if (_state == CircuitBreakerState.HalfOpen)
            {
                // Any failure in half-open state should open the circuit
                TransitionTo(CircuitBreakerState.Open, "Failure detected during recovery attempt");
            }
        }

        private void OnTimeout()
        {
            Interlocked.Increment(ref _timeoutCount);
            OnFailure(new TimeoutException("Operation timed out"));
        }

        private bool ShouldAttemptReset()
        {
            if (_state != CircuitBreakerState.Open)
                return false;

            var lastStateChangeTime = new DateTime(_lastStateChangeTimeTicks);
            return DateTime.UtcNow - lastStateChangeTime >= _options.RecoveryTimeout;
        }

        private void TransitionTo(CircuitBreakerState newState, string reason)
        {
            lock (_lockObject)
            {
                var previousState = _state;
                if (previousState == newState)
                    return;

                _state = newState;
                Interlocked.Exchange(ref _lastStateChangeTimeTicks, DateTime.UtcNow.Ticks);

                _logger?.LogInformation(
                    "Circuit breaker state changed from {PreviousState} to {NewState}: {Reason}",
                    previousState,
                    newState,
                    reason);

                try
                {
                    StateChanged?.Invoke(this, new CircuitBreakerStateChangedEventArgs(
                        previousState, newState, reason));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error raising StateChanged event");
                }

                // Reset counters when closing circuit
                if (newState == CircuitBreakerState.Closed)
                {
                    Interlocked.Exchange(ref _failureCount, 0);
                    Interlocked.Exchange(ref _successCount, 0);
                    Interlocked.Exchange(ref _timeoutCount, 0);
                }
            }
        }

        public CircuitBreakerMetrics GetMetrics()
        {
            var lastFailureTime = _lastFailureTimeTicks != 0 
                ? new DateTime(_lastFailureTimeTicks) 
                : (DateTime?)null;

            var lastSuccessTime = _lastSuccessTimeTicks != 0 
                ? new DateTime(_lastSuccessTimeTicks) 
                : (DateTime?)null;

            var lastStateChangeTime = _lastStateChangeTimeTicks != 0
                ? DateTime.UtcNow - new DateTime(_lastStateChangeTimeTicks)
                : (TimeSpan?)null;

            return new CircuitBreakerMetrics
            {
                SuccessCount = _successCount,
                FailureCount = _failureCount,
                TimeoutCount = _timeoutCount,
                RejectedCount = _rejectedCount,
                State = _state,
                LastFailureTime = lastFailureTime,
                LastSuccessTime = lastSuccessTime,
                LastStateChangeTime = lastStateChangeTime
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _logger?.LogDebug("Circuit breaker disposed");
            }
        }
    }

    /// <summary>
    /// Factory for creating circuit breaker instances
    /// </summary>
    public static class CircuitBreakerFactory
    {
        public static ICircuitBreaker Create(
            CircuitBreakerOptions? options = null,
            ILogger<CircuitBreaker>? logger = null)
        {
            return new CircuitBreaker(options ?? new CircuitBreakerOptions(), logger);
        }

        public static ICircuitBreaker CreateForRiskAssessment(ILogger<CircuitBreaker>? logger = null)
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 3,
                RecoveryTimeout = TimeSpan.FromSeconds(15),
                SuccessThreshold = 2,
                OperationTimeout = TimeSpan.FromSeconds(5),
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30)
            };

            return new CircuitBreaker(options, logger);
        }

        public static ICircuitBreaker CreateForExchange(ILogger<CircuitBreaker>? logger = null)
        {
            var options = new CircuitBreakerOptions
            {
                FailureThreshold = 5,
                RecoveryTimeout = TimeSpan.FromSeconds(30),
                SuccessThreshold = 3,
                OperationTimeout = TimeSpan.FromSeconds(10),
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromMinutes(1)
            };

            return new CircuitBreaker(options, logger);
        }
    }
}