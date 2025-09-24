using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Strategies.Models;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using System.Collections.Concurrent;

namespace TradingEngine.Execution.Pipeline
{
    /// <summary>
    /// Coordinates order processing by managing signal intake and pipeline orchestration
    /// Replaces the traditional OrderRouter with a modern, scalable architecture
    /// Uses high-performance channels for signal queueing and processing
    /// </summary>
    public sealed class OrderProcessingCoordinator : IDisposable
    {
        private readonly IOrderProcessingPipeline _pipeline;
        private readonly ILogger<OrderProcessingCoordinator>? _logger;
        private readonly OrderProcessingCoordinatorOptions _options;
        
        // High-performance signal processing
        private readonly Channel<Signal> _signalChannel;
        private readonly ChannelWriter<Signal> _signalWriter;
        private readonly ChannelReader<Signal> _signalReader;
        
        // Processing control
        private readonly CancellationTokenSource _processingCts;
        private readonly SemaphoreSlim _processingCapacity;
        private readonly List<Task> _processingTasks;
        
        // Statistics and monitoring
        private readonly OrderCoordinatorMetrics _metrics;
        private volatile bool _isRunning;
        private bool _disposed;

        public event EventHandler<SignalReceivedEventArgs>? SignalReceived;
        public event EventHandler<SignalProcessedEventArgs>? SignalProcessed;
        public event EventHandler<SignalRejectedEventArgs>? SignalRejected;

        public OrderProcessingCoordinator(
            IOrderProcessingPipeline pipeline,
            OrderProcessingCoordinatorOptions? options = null,
            ILogger<OrderProcessingCoordinator>? logger = null)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _options = options ?? new OrderProcessingCoordinatorOptions();
            _logger = logger;

            // Initialize high-performance channel for signal processing
            var channelOptions = new BoundedChannelOptions(_options.MaxQueuedSignals)
            {
                FullMode = _options.ChannelFullMode,
                SingleReader = false, // Allow multiple processing tasks
                SingleWriter = false, // Allow multiple strategy engines
                AllowSynchronousContinuations = false // Prevent blocking
            };
            
            _signalChannel = Channel.CreateBounded<Signal>(channelOptions);
            _signalWriter = _signalChannel.Writer;
            _signalReader = _signalChannel.Reader;

            _processingCts = new CancellationTokenSource();
            _processingCapacity = new SemaphoreSlim(_options.MaxConcurrentProcessing, _options.MaxConcurrentProcessing);
            _processingTasks = new List<Task>();
            _metrics = new OrderCoordinatorMetrics();

            // Subscribe to pipeline events
            _pipeline.ProcessingCompleted += OnPipelineProcessingCompleted;
            _pipeline.ProcessingError += OnPipelineProcessingError;
        }

        /// <summary>
        /// Submit a signal for order processing
        /// Non-blocking, high-throughput signal intake
        /// </summary>
        public bool SubmitSignal(Signal signal)
        {
            if (_disposed || !_isRunning)
            {
                _logger?.LogWarning("Cannot submit signal - coordinator is not running");
                return false;
            }

            if (signal == null)
            {
                _logger?.LogWarning("Attempted to submit null signal");
                return false;
            }

            if (!signal.IsValid())
            {
                _logger?.LogWarning("Attempted to submit invalid signal: {Signal}", signal);
                _metrics.IncrementRejectedSignals("Invalid signal");
                RaiseSignalRejected(signal, "Invalid signal");
                return false;
            }

            try
            {
                // Attempt to write to channel (non-blocking)
                if (_signalWriter.TryWrite(signal))
                {
                    _metrics.IncrementReceivedSignals();
                    RaiseSignalReceived(signal);
                    
                    _logger?.LogDebug(
                        "Signal queued for processing: {Symbol} {Side} {Quantity}",
                        signal.Symbol,
                        signal.Side,
                        signal.Quantity);
                    
                    return true;
                }
                else
                {
                    // Channel is full - signal rejected
                    _logger?.LogWarning(
                        "Signal queue is full - rejecting signal: {Symbol} {Side} {Quantity}",
                        signal.Symbol,
                        signal.Side,
                        signal.Quantity);
                    
                    _metrics.IncrementRejectedSignals("Queue full");
                    RaiseSignalRejected(signal, "Signal queue is full");
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                // Writer is closed
                _logger?.LogError("Cannot submit signal - signal channel is closed");
                _metrics.IncrementRejectedSignals("Channel closed");
                RaiseSignalRejected(signal, "Signal processing is shutting down");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error submitting signal");
                _metrics.IncrementRejectedSignals($"Error: {ex.Message}");
                RaiseSignalRejected(signal, $"Submission error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get current processing statistics
        /// </summary>
        public OrderCoordinatorMetrics GetMetrics() => _metrics.GetSnapshot();

        /// <summary>
        /// Get detailed health information
        /// </summary>
        public async Task<CoordinatorHealthInfo> GetHealthAsync()
        {
            var pipelineHealth = await _pipeline.CheckHealthAsync();
            var queueSize = _signalReader.CanCount ? _signalReader.Count : -1;
            
            return new CoordinatorHealthInfo
            {
                IsRunning = _isRunning,
                QueueSize = queueSize,
                MaxQueueSize = _options.MaxQueuedSignals,
                ActiveProcessingTasks = _processingTasks.Count(t => !t.IsCompleted),
                MaxConcurrentProcessing = _options.MaxConcurrentProcessing,
                PipelineHealth = pipelineHealth,
                Metrics = GetMetrics(),
                LastHealthCheck = DateTime.UtcNow
            };
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OrderProcessingCoordinator));

            if (_isRunning)
            {
                _logger?.LogWarning("Order processing coordinator is already running");
                return;
            }

            try
            {
                _logger?.LogInformation("Starting order processing coordinator");

                // Start the pipeline
                await _pipeline.StartAsync(cancellationToken);

                // Start signal processing tasks
                for (int i = 0; i < _options.ProcessingTaskCount; i++)
                {
                    var taskName = $"SignalProcessor-{i + 1}";
                    var processingTask = ProcessSignalsAsync(taskName, _processingCts.Token);
                    _processingTasks.Add(processingTask);
                }

                _isRunning = true;
                _metrics.Reset(); // Reset metrics on startup

                _logger?.LogInformation(
                    "Order processing coordinator started with {TaskCount} processing tasks, " +
                    "max queue size {MaxQueue}, max concurrency {MaxConcurrency}",
                    _options.ProcessingTaskCount,
                    _options.MaxQueuedSignals,
                    _options.MaxConcurrentProcessing);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start order processing coordinator");
                await StopAsync(cancellationToken);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_isRunning)
            {
                _logger?.LogWarning("Order processing coordinator is not running");
                return;
            }

            try
            {
                _logger?.LogInformation("Stopping order processing coordinator");

                _isRunning = false;

                // Complete the signal channel (no more signals accepted)
                _signalWriter.TryComplete();

                // Cancel processing
                _processingCts.Cancel();

                // Wait for processing tasks to complete
                try
                {
                    using var timeoutCts = new CancellationTokenSource(_options.ShutdownTimeout);
                    await Task.WhenAll(_processingTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning("Some processing tasks did not complete within shutdown timeout");
                }

                // Stop the pipeline
                await _pipeline.StopAsync(cancellationToken);

                _logger?.LogInformation("Order processing coordinator stopped");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping order processing coordinator");
                throw;
            }
        }

        /// <summary>
        /// Background task that processes signals from the channel
        /// </summary>
        private async Task ProcessSignalsAsync(string taskName, CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Signal processing task {TaskName} started", taskName);

            try
            {
                await foreach (var signal in _signalReader.ReadAllAsync(cancellationToken))
                {
                    // Acquire processing capacity
                    await _processingCapacity.WaitAsync(cancellationToken);

                    try
                    {
                        var processedSignal = await ProcessSingleSignalAsync(signal, cancellationToken);
                        RaiseSignalProcessed(signal, processedSignal);
                    }
                    finally
                    {
                        _processingCapacity.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                _logger?.LogDebug("Signal processing task {TaskName} cancelled", taskName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in signal processing task {TaskName}", taskName);
            }
            finally
            {
                _logger?.LogDebug("Signal processing task {TaskName} completed", taskName);
            }
        }

        /// <summary>
        /// Process a single signal through the pipeline
        /// </summary>
        private async Task<OrderProcessingResult> ProcessSingleSignalAsync(Signal signal, CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                _logger?.LogDebug(
                    "Processing signal: {Symbol} {Side} {Quantity} (confidence: {Confidence:P2})",
                    signal.Symbol,
                    signal.Side,
                    signal.Quantity,
                    signal.Confidence);

                // Create processing context
                var context = new OrderProcessingContext(signal);

                // Process through pipeline
                var result = await _pipeline.ProcessSignalAsync(signal, context, cancellationToken);

                var processingTime = DateTime.UtcNow - startTime;

                if (result.IsSuccess)
                {
                    _metrics.IncrementSuccessfulProcessing(processingTime);
                    
                    _logger?.LogInformation(
                        "Order processing successful: OrderId {OrderId} for {Symbol} {Side} {Quantity} in {ProcessingTime}ms",
                        result.OrderId,
                        signal.Symbol,
                        signal.Side,
                        signal.Quantity,
                        processingTime.TotalMilliseconds);
                }
                else
                {
                    _metrics.IncrementFailedProcessing(processingTime);
                    
                    _logger?.LogWarning(
                        "Order processing failed for {Symbol} {Side} {Quantity}: {ErrorMessage}",
                        signal.Symbol,
                        signal.Side,
                        signal.Quantity,
                        result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                var processingTime = DateTime.UtcNow - startTime;
                _metrics.IncrementFailedProcessing(processingTime);

                _logger?.LogError(ex,
                    "Unhandled exception processing signal {Symbol} {Side} {Quantity}",
                    signal.Symbol,
                    signal.Side,
                    signal.Quantity);

                return OrderProcessingResult.Failure(
                    $"Unhandled processing exception: {ex.Message}",
                    processingTime,
                    correlationId: $"signal_{signal.GeneratedAt.UnixMilliseconds}");
            }
        }

        private void OnPipelineProcessingCompleted(object? sender, OrderProcessingCompletedEventArgs e)
        {
            _logger?.LogDebug(
                "Pipeline processing completed for signal correlation {CorrelationId}",
                e.Result.CorrelationId);
        }

        private void OnPipelineProcessingError(object? sender, OrderProcessingErrorEventArgs e)
        {
            _logger?.LogError(
                "Pipeline processing error in stage {StageName} for signal correlation {CorrelationId}: {ErrorMessage}",
                e.StageName,
                e.CorrelationId,
                e.Exception.Message);
        }

        private void RaiseSignalReceived(Signal signal)
        {
            try
            {
                SignalReceived?.Invoke(this, new SignalReceivedEventArgs { Signal = signal });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising SignalReceived event");
            }
        }

        private void RaiseSignalProcessed(Signal signal, OrderProcessingResult result)
        {
            try
            {
                SignalProcessed?.Invoke(this, new SignalProcessedEventArgs { Signal = signal, Result = result });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising SignalProcessed event");
            }
        }

        private void RaiseSignalRejected(Signal signal, string reason)
        {
            try
            {
                SignalRejected?.Invoke(this, new SignalRejectedEventArgs { Signal = signal, Reason = reason });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising SignalRejected event");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_isRunning)
                {
                    StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                // Unsubscribe from pipeline events
                _pipeline.ProcessingCompleted -= OnPipelineProcessingCompleted;
                _pipeline.ProcessingError -= OnPipelineProcessingError;

                _processingCts?.Dispose();
                _processingCapacity?.Dispose();
                _pipeline?.Dispose();

                _logger?.LogInformation("Order processing coordinator disposed");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during coordinator disposal");
            }
        }
    }

    /// <summary>
    /// Configuration options for the order processing coordinator
    /// </summary>
    public sealed class OrderProcessingCoordinatorOptions
    {
        /// <summary>
        /// Maximum number of signals that can be queued for processing
        /// </summary>
        public int MaxQueuedSignals { get; set; } = 10000;

        /// <summary>
        /// Maximum number of signals that can be processed concurrently
        /// </summary>
        public int MaxConcurrentProcessing { get; set; } = Environment.ProcessorCount * 2;

        /// <summary>
        /// Number of background processing tasks
        /// </summary>
        public int ProcessingTaskCount { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Behavior when signal channel is full
        /// </summary>
        public BoundedChannelFullMode ChannelFullMode { get; set; } = BoundedChannelFullMode.DropOldest;

        /// <summary>
        /// Maximum time to wait for graceful shutdown
        /// </summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Metrics for order processing coordinator
    /// </summary>
    public sealed class OrderCoordinatorMetrics
    {
        private long _receivedSignals;
        private long _rejectedSignals;
        private long _successfulProcessing;
        private long _failedProcessing;
        private long _totalProcessingTimeMs;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly ConcurrentBag<string> _rejectionReasons = new();

        public long ReceivedSignals => _receivedSignals;
        public long RejectedSignals => _rejectedSignals;
        public long SuccessfulProcessing => _successfulProcessing;
        public long FailedProcessing => _failedProcessing;
        public long TotalProcessed => _successfulProcessing + _failedProcessing;
        public decimal SuccessRate => TotalProcessed == 0 ? 0 : (decimal)_successfulProcessing / TotalProcessed;
        public double AverageProcessingTimeMs => TotalProcessed == 0 ? 0 : (double)_totalProcessingTimeMs / TotalProcessed;
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;

        public void IncrementReceivedSignals() => Interlocked.Increment(ref _receivedSignals);
        
        public void IncrementRejectedSignals(string reason)
        {
            Interlocked.Increment(ref _rejectedSignals);
            _rejectionReasons.Add(reason);
        }

        public void IncrementSuccessfulProcessing(TimeSpan processingTime)
        {
            Interlocked.Increment(ref _successfulProcessing);
            Interlocked.Add(ref _totalProcessingTimeMs, (long)processingTime.TotalMilliseconds);
        }

        public void IncrementFailedProcessing(TimeSpan processingTime)
        {
            Interlocked.Increment(ref _failedProcessing);
            Interlocked.Add(ref _totalProcessingTimeMs, (long)processingTime.TotalMilliseconds);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _receivedSignals, 0);
            Interlocked.Exchange(ref _rejectedSignals, 0);
            Interlocked.Exchange(ref _successfulProcessing, 0);
            Interlocked.Exchange(ref _failedProcessing, 0);
            Interlocked.Exchange(ref _totalProcessingTimeMs, 0);
        }

        public OrderCoordinatorMetrics GetSnapshot()
        {
            return new OrderCoordinatorMetrics
            {
                _receivedSignals = this._receivedSignals,
                _rejectedSignals = this._rejectedSignals,
                _successfulProcessing = this._successfulProcessing,
                _failedProcessing = this._failedProcessing,
                _totalProcessingTimeMs = this._totalProcessingTimeMs
            };
        }
    }

    /// <summary>
    /// Health information for the coordinator
    /// </summary>
    public sealed record CoordinatorHealthInfo
    {
        public bool IsRunning { get; init; }
        public int QueueSize { get; init; }
        public int MaxQueueSize { get; init; }
        public int ActiveProcessingTasks { get; init; }
        public int MaxConcurrentProcessing { get; init; }
        public HealthCheckResult PipelineHealth { get; init; } = null!;
        public OrderCoordinatorMetrics Metrics { get; init; } = null!;
        public DateTime LastHealthCheck { get; init; }
    }

    // Event argument classes
    public sealed class SignalReceivedEventArgs : EventArgs
    {
        public Signal Signal { get; set; } = null!;
    }

    public sealed class SignalProcessedEventArgs : EventArgs
    {
        public Signal Signal { get; set; } = null!;
        public OrderProcessingResult Result { get; set; } = null!;
    }

    public sealed class SignalRejectedEventArgs : EventArgs
    {
        public Signal Signal { get; set; } = null!;
        public string Reason { get; set; } = string.Empty;
    }
}