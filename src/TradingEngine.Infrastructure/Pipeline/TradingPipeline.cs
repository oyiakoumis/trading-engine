using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Execution.Pipeline;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.MarketData.Interfaces;
using TradingEngine.MarketData.Processors;
using TradingEngine.Risk.Interfaces;
using TradingEngine.Risk.Services;
using TradingEngine.Strategies.Engine;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Modern trading pipeline with redesigned order processing architecture
    /// Uses the new OrderProcessingCoordinator for high-performance signal-to-order processing
    /// Implements event-driven architecture with CQRS patterns
    /// </summary>
    public class TradingPipeline : IPipelineOrchestrator, IAsyncDisposable
    {
        // Core components
        private readonly IMarketDataProvider _marketDataProvider;
        private readonly MarketDataProcessor _marketDataProcessor;
        private readonly StrategyEngine _strategyEngine;
        private readonly OrderProcessingCoordinator _orderProcessingCoordinator;
        private readonly IExchange _exchange;
        private readonly IRiskManager _riskManager;
        private readonly PnLTracker _pnlTracker;
        private readonly IEventBus _eventBus;
        private readonly ILogger<TradingPipeline>? _logger;

        // Helper classes
        private readonly PipelineStatisticsCollector _statisticsCollector;
        private readonly ReactiveStrategyProcessor _strategyProcessor;
        private readonly ComponentCoordinator _componentCoordinator;

        // Thread management
        private readonly CancellationTokenSource _shutdownCts;
        private readonly Task _marketDataTask;
        private readonly SemaphoreSlim _startupSemaphore;

        // Thread-safe state management
        private readonly ConcurrentDictionary<Symbol, Position> _positions;
        private volatile bool _isRunning;
        private volatile bool _disposed;

        // Configuration
        public int TickHistorySize { get; set; } = PipelineConstants.DefaultTickHistorySize;
        public decimal InitialCapital { get; set; } = 100000m;

        public event EventHandler<PipelineEventArgs>? PipelineEvent;

        public bool IsRunning => _isRunning;

        public TradingPipeline(
            IMarketDataProvider marketDataProvider,
            MarketDataProcessor marketDataProcessor,
            StrategyEngine strategyEngine,
            OrderProcessingCoordinator orderProcessingCoordinator,
            IExchange exchange,
            IRiskManager riskManager,
            PnLTracker pnlTracker,
            IEventBus eventBus,
            ILogger<TradingPipeline>? logger = null)
        {
            _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
            _marketDataProcessor = marketDataProcessor ?? throw new ArgumentNullException(nameof(marketDataProcessor));
            _strategyEngine = strategyEngine ?? throw new ArgumentNullException(nameof(strategyEngine));
            _orderProcessingCoordinator = orderProcessingCoordinator ?? throw new ArgumentNullException(nameof(orderProcessingCoordinator));
            _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
            _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
            _pnlTracker = pnlTracker ?? throw new ArgumentNullException(nameof(pnlTracker));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger;

            // Initialize collections
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _startupSemaphore = new SemaphoreSlim(0, PipelineConstants.ProcessingThreadCount);
            _shutdownCts = new CancellationTokenSource();

            // Initialize helper classes
            _statisticsCollector = new PipelineStatisticsCollector();
            _strategyProcessor = new ReactiveStrategyProcessor(_strategyEngine, _eventBus, _statisticsCollector, null);
            _componentCoordinator = new ComponentCoordinator(_exchange, _riskManager, _pnlTracker, _eventBus, _statisticsCollector, null);

            // Initialize event handlers
            InitializeEventHandlers();

            // Start processing task for market data
            _marketDataTask = ProcessMarketDataAsync(_shutdownCts.Token);
        }

        /// <summary>
        /// Start the trading pipeline with modern order processing
        /// </summary>
        public async Task StartAsync(IEnumerable<Symbol> symbols)
        {
            if (_isRunning)
            {
                _logger?.LogWarning("Trading pipeline is already running");
                return;
            }

            try
            {
                _logger?.LogInformation("Starting modern trading pipeline with capital {Capital:C}", InitialCapital);

                // Start statistics collection
                _statisticsCollector.Start();

                // Start components in dependency order
                await _marketDataProvider.ConnectAsync();
                await _marketDataProvider.SubscribeAsync(symbols);

                _marketDataProcessor.Start();
                _strategyEngine.Start();
                
                // Start the new order processing coordinator
                await _orderProcessingCoordinator.StartAsync(_shutdownCts.Token);
                
                _exchange.Start();

                _isRunning = true;

                // Release semaphore for waiting threads
                for (int i = 0; i < PipelineConstants.ProcessingThreadCount; i++)
                {
                    _startupSemaphore.Release();
                }

                await _eventBus.PublishAsync(new SystemEvent("INFO", "Modern trading pipeline started", "PipelineV2"));
                RaisePipelineEvent("Modern Pipeline Started", PipelineEventType.Started);

                _logger?.LogInformation(
                    "Modern trading pipeline started successfully with {Symbols} symbols",
                    string.Join(", ", symbols.Select(s => s.Value)));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start modern trading pipeline");
                await StopAsync(); // Cleanup on failure
                throw;
            }
        }

        /// <summary>
        /// Stop the trading pipeline gracefully
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
            {
                _logger?.LogWarning("Trading pipeline is not running");
                return;
            }

            try
            {
                _logger?.LogInformation("Stopping modern trading pipeline");

                _isRunning = false;
                _statisticsCollector.Stop();

                // Stop components in reverse dependency order
                _strategyEngine.Stop();
                
                // Stop the order processing coordinator
                await _orderProcessingCoordinator.StopAsync(_shutdownCts.Token);
                
                _exchange.Stop();
                await _marketDataProcessor.StopAsync();
                await _marketDataProvider.DisconnectAsync();

                // Stop helper processors
                await _strategyProcessor.StopAsync();
                await _componentCoordinator.StopAsync();

                // Cancel processing tasks
                _shutdownCts.Cancel();

                // Wait for market data task to complete with timeout
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(PipelineConstants.ComponentStopTimeoutSeconds));
                try
                {
                    await _marketDataTask.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning("Market data task did not stop within timeout");
                }

                await _eventBus.PublishAsync(new SystemEvent("INFO", "Modern trading pipeline stopped", "PipelineV2"));
                RaisePipelineEvent("Modern Pipeline Stopped", PipelineEventType.Stopped);

                _logger?.LogInformation("Modern trading pipeline stopped successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping modern trading pipeline");
                throw;
            }
        }

        /// <summary>
        /// Process market data with enhanced error handling and performance monitoring
        /// </summary>
        private async Task ProcessMarketDataAsync(CancellationToken cancellationToken)
        {
            await _startupSemaphore.WaitAsync(cancellationToken);
            _logger?.LogInformation("Market data processing task started");

            try
            {
                await foreach (var tick in _marketDataProvider.StreamTicksAsync(cancellationToken))
                {
                    try
                    {
                        var processingStart = DateTime.UtcNow;

                        // Process tick through the processor
                        await _marketDataProcessor.SubmitTickAsync(tick, cancellationToken);

                        // Update strategy engine with tick history from processor
                        var tickHistory = _marketDataProcessor.GetTickHistory(tick.Symbol).ToList();
                        await _strategyEngine.UpdateMarketDataAsync(tick, tickHistory);

                        // Update exchange prices for execution
                        _exchange.UpdateMarketPrice(tick.Symbol, tick.Bid, tick.Ask);

                        // Update P&L tracker
                        await _pnlTracker.UpdateMarketPriceAsync(tick.Symbol, tick.MidPrice);

                        // Publish tick received event
                        await _eventBus.PublishAsync(new TickReceivedEvent(tick), cancellationToken);

                        _statisticsCollector.IncrementTicksProcessed();

                        // Log performance metrics periodically
                        var processingTime = DateTime.UtcNow - processingStart;
                        if (processingTime.TotalMilliseconds > 10) // Log slow ticks
                        {
                            _logger?.LogDebug(
                                "Tick processing for {Symbol} took {ProcessingTime}ms",
                                tick.Symbol,
                                processingTime.TotalMilliseconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing tick for {Symbol}: {Price}", tick.Symbol, tick.MidPrice);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Market data processing failed");
                RaisePipelineEvent($"Market Data Error: {ex.Message}", PipelineEventType.Error);
            }
            finally
            {
                _logger?.LogInformation("Market data processing task stopped");
            }
        }

        /// <summary>
        /// Initialize event handlers for the modern pipeline
        /// </summary>
        private void InitializeEventHandlers()
        {
            // Subscribe to risk breaches
            _riskManager.RiskBreached += OnRiskBreached;

            // Connect signal flow to new order processing coordinator
            _strategyEngine.SignalGenerated += OnSignalGenerated;

            // Subscribe to order processing coordinator events
            _orderProcessingCoordinator.SignalReceived += OnSignalReceived;
            _orderProcessingCoordinator.SignalProcessed += OnSignalProcessed;
            _orderProcessingCoordinator.SignalRejected += OnSignalRejected;
        }

        /// <summary>
        /// Handle signals generated by strategies using the new coordinator
        /// </summary>
        private void OnSignalGenerated(object? sender, Signal signal)
        {
            try
            {
                var submitted = _orderProcessingCoordinator.SubmitSignal(signal);
                
                if (!submitted)
                {
                    _logger?.LogWarning(
                        "Failed to submit signal for {Symbol} {Side} {Quantity}",
                        signal.Symbol,
                        signal.Side,
                        signal.Quantity);
                }

                _statisticsCollector.IncrementSignalsGenerated();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error submitting signal for {Symbol}", signal.Symbol);
            }
        }

        /// <summary>
        /// Handle signal received by coordinator
        /// </summary>
        private void OnSignalReceived(object? sender, SignalReceivedEventArgs e)
        {
            _logger?.LogDebug(
                "Signal received for processing: {Symbol} {Side} {Quantity}",
                e.Signal.Symbol,
                e.Signal.Side,
                e.Signal.Quantity);
        }

        /// <summary>
        /// Handle successfully processed signals
        /// </summary>
        private void OnSignalProcessed(object? sender, SignalProcessedEventArgs e)
        {
            if (e.Result.IsSuccess)
            {
                _logger?.LogInformation(
                    "Signal processed successfully: OrderId {OrderId} for {Symbol}",
                    e.Result.OrderId,
                    e.Signal.Symbol);

                _statisticsCollector.IncrementOrdersExecuted();
            }
            else
            {
                _logger?.LogWarning(
                    "Signal processing failed for {Symbol}: {ErrorMessage}",
                    e.Signal.Symbol,
                    e.Result.ErrorMessage);
            }
        }

        /// <summary>
        /// Handle rejected signals
        /// </summary>
        private void OnSignalRejected(object? sender, SignalRejectedEventArgs e)
        {
            _logger?.LogWarning(
                "Signal rejected for {Symbol} {Side} {Quantity}: {Reason}",
                e.Signal.Symbol,
                e.Signal.Side,
                e.Signal.Quantity,
                e.Reason);
        }

        /// <summary>
        /// Handle risk breaches with enhanced monitoring
        /// </summary>
        private void OnRiskBreached(object? sender, RiskBreachEventArgs args)
        {
            try
            {
                _logger?.LogWarning(
                    "Risk breach detected: {Description} (Severity: {Severity})",
                    args.Breach.Description,
                    args.Breach.Severity);

                if (args.RequiresImmediateAction)
                {
                    _logger?.LogCritical(
                        "CRITICAL: Immediate action required for risk breach: {Description}",
                        args.Breach.Description);
                    
                    RaisePipelineEvent($"Critical Risk: {args.Breach.Description}", PipelineEventType.RiskBreach);
                    
                    // In a production system, this might trigger automatic position liquidation
                    // or other emergency risk management procedures
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling risk breach");
            }
        }

        /// <summary>
        /// Get comprehensive pipeline statistics including new order processing metrics
        /// </summary>
        public PipelineStatistics GetStatistics()
        {
            var activePositions = _positions.Count(p => p.Value.IsOpen);
            var eventBusStats = _eventBus.GetStatistics();
            var coordinatorMetrics = _orderProcessingCoordinator.GetMetrics();
            
            var stats = _statisticsCollector.GetStatistics(activePositions, eventBusStats);
            
            // Enhance statistics with order processing coordinator metrics
            return new EnhancedPipelineStatistics
            {
                IsRunning = stats.IsRunning,
                Uptime = stats.Uptime,
                TicksProcessed = stats.TicksProcessed,
                SignalsGenerated = stats.SignalsGenerated,
                OrdersExecuted = stats.OrdersExecuted,
                ActivePositions = activePositions,
                EventBusStats = stats.EventBusStats,
                
                // New order processing metrics
                SignalsReceived = coordinatorMetrics.ReceivedSignals,
                SignalsRejected = coordinatorMetrics.RejectedSignals,
                SuccessfulProcessing = coordinatorMetrics.SuccessfulProcessing,
                FailedProcessing = coordinatorMetrics.FailedProcessing,
                ProcessingSuccessRate = coordinatorMetrics.SuccessRate,
                AverageProcessingTime = coordinatorMetrics.AverageProcessingTimeMs
            };
        }

        /// <summary>
        /// Get detailed health information for the entire pipeline
        /// </summary>
        public async Task<PipelineHealthInfo> GetHealthAsync()
        {
            var coordinatorHealth = await _orderProcessingCoordinator.GetHealthAsync();
            
            return new PipelineHealthInfo
            {
                IsRunning = _isRunning,
                ComponentsHealthy = !_disposed,
                OrderProcessingHealth = coordinatorHealth,
                Statistics = (EnhancedPipelineStatistics)GetStatistics(),
                LastHealthCheck = DateTime.UtcNow
            };
        }

        private void RaisePipelineEvent(string message, PipelineEventType eventType)
        {
            try
            {
                PipelineEvent?.Invoke(this, new PipelineEventArgs
                {
                    Message = message,
                    EventType = eventType,
                    Timestamp = Timestamp.Now
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising pipeline event");
            }
        }

        /// <summary>
        /// Async dispose pattern implementation
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            _disposed = true;

            try
            {
                if (_isRunning)
                {
                    await StopAsync();
                }

                // Dispose helper classes
                _strategyProcessor?.Dispose();
                _componentCoordinator?.Dispose();

                // Unsubscribe from events
                _riskManager.RiskBreached -= OnRiskBreached;
                _strategyEngine.SignalGenerated -= OnSignalGenerated;
                
                if (_orderProcessingCoordinator != null)
                {
                    _orderProcessingCoordinator.SignalReceived -= OnSignalReceived;
                    _orderProcessingCoordinator.SignalProcessed -= OnSignalProcessed;
                    _orderProcessingCoordinator.SignalRejected -= OnSignalRejected;
                }

                _shutdownCts.Dispose();
                _startupSemaphore.Dispose();

                // Dispose injected components if they implement IDisposable
                if (_marketDataProcessor is IDisposable disposableProcessor)
                    disposableProcessor.Dispose();

                if (_strategyEngine is IDisposable disposableStrategy)
                    disposableStrategy.Dispose();

                _orderProcessingCoordinator?.Dispose();
                _exchange?.Dispose();
                _pnlTracker?.Dispose();

                if (_riskManager is IDisposable disposableRisk)
                    disposableRisk.Dispose();

                if (_eventBus is IDisposable disposableEventBus)
                    disposableEventBus.Dispose();

                _logger?.LogInformation("Modern trading pipeline disposed successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during modern pipeline disposal");
            }
        }

        /// <summary>
        /// Synchronous dispose for compatibility
        /// </summary>
        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Enhanced pipeline statistics with order processing metrics
    /// </summary>
    public class EnhancedPipelineStatistics : PipelineStatistics
    {
        public long SignalsReceived { get; set; }
        public long SignalsRejected { get; set; }
        public long SuccessfulProcessing { get; set; }
        public long FailedProcessing { get; set; }
        public decimal ProcessingSuccessRate { get; set; }
        public double AverageProcessingTime { get; set; }

        public override string ToString()
        {
            return base.ToString() + 
                   $" | Signals: Received={SignalsReceived}, Rejected={SignalsRejected} | " +
                   $"Processing: Success={SuccessfulProcessing}, Failed={FailedProcessing}, " +
                   $"Rate={ProcessingSuccessRate:P2}, AvgTime={AverageProcessingTime:F2}ms";
        }
    }

    /// <summary>
    /// Comprehensive pipeline health information
    /// </summary>
    public sealed record PipelineHealthInfo
    {
        public bool IsRunning { get; init; }
        public bool ComponentsHealthy { get; init; }
        public CoordinatorHealthInfo OrderProcessingHealth { get; init; } = null!;
        public EnhancedPipelineStatistics Statistics { get; init; } = null!;
        public DateTime LastHealthCheck { get; init; }
        
        public bool IsHealthy => IsRunning && ComponentsHealthy && OrderProcessingHealth.PipelineHealth.IsHealthy;
    }

    /// <summary>
    /// Pipeline statistics (base class for compatibility)
    /// </summary>
    public class PipelineStatistics
    {
        public bool IsRunning { get; set; }
        public TimeSpan Uptime { get; set; }
        public long TicksProcessed { get; set; }
        public long SignalsGenerated { get; set; }
        public long OrdersExecuted { get; set; }
        public int ActivePositions { get; set; }
        public EventBusStatistics? EventBusStats { get; set; }

        public override string ToString()
        {
            return $"Pipeline: {(IsRunning ? "Running" : "Stopped")} | Uptime: {Uptime:hh\\:mm\\:ss} | " +
                   $"Ticks: {TicksProcessed} | Signals: {SignalsGenerated} | " +
                   $"Orders: {OrdersExecuted} | Positions: {ActivePositions}";
        }
    }

    /// <summary>
    /// Pipeline event arguments
    /// </summary>
    public class PipelineEventArgs : EventArgs
    {
        public string Message { get; set; } = string.Empty;
        public PipelineEventType EventType { get; set; }
        public Timestamp Timestamp { get; set; }
    }

    /// <summary>
    /// Pipeline event types
    /// </summary>
    public enum PipelineEventType
    {
        Started,
        Stopped,
        RiskBreach,
        Error,
        Warning,
        Info
    }

}