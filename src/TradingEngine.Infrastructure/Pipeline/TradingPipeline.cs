using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Execution.Services;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.MarketData.Interfaces;
using TradingEngine.MarketData.Processors;
using TradingEngine.Risk.Interfaces;
using TradingEngine.Risk.Services;
using TradingEngine.Strategies.Engine;

namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Refactored trading pipeline that orchestrates all components
    /// Uses dependency injection and reactive patterns for better performance and maintainability
    /// </summary>
    public class TradingPipeline : IPipelineOrchestrator, IAsyncDisposable
    {
        // Core components - now injected dependencies
        private readonly IMarketDataProvider _marketDataProvider;
        private readonly MarketDataProcessor _marketDataProcessor;
        private readonly StrategyEngine _strategyEngine;
        private readonly IOrderManager _orderManager;
        private readonly OrderRouter _orderRouter;
        private readonly IExchange _exchange;
        private readonly IRiskManager _riskManager;
        private readonly PnLTracker _pnlTracker;
        private readonly IEventBus _eventBus;
        private readonly ILogger<TradingPipeline>? _logger;

        // Helper classes for better separation of concerns
        private readonly PipelineStatisticsCollector _statisticsCollector;
        private readonly ReactiveStrategyProcessor _strategyProcessor;
        private readonly ComponentCoordinator _componentCoordinator;

        // Thread management with proper cancellation
        private readonly CancellationTokenSource _shutdownCts;
        private readonly Task _marketDataTask;
        private readonly SemaphoreSlim _startupSemaphore;

        // Thread-safe state management
        private readonly ConcurrentDictionary<Symbol, ConcurrentQueue<Tick>> _tickHistory;
        private readonly ConcurrentDictionary<Symbol, Position> _positions;
        private readonly SemaphoreSlim _tickHistorySemaphore;
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
            IOrderManager orderManager,
            OrderRouter orderRouter,
            IExchange exchange,
            IRiskManager riskManager,
            PnLTracker pnlTracker,
            IEventBus eventBus,
            ILogger<TradingPipeline>? logger = null)
        {
            _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
            _marketDataProcessor = marketDataProcessor ?? throw new ArgumentNullException(nameof(marketDataProcessor));
            _strategyEngine = strategyEngine ?? throw new ArgumentNullException(nameof(strategyEngine));
            _orderManager = orderManager ?? throw new ArgumentNullException(nameof(orderManager));
            _orderRouter = orderRouter ?? throw new ArgumentNullException(nameof(orderRouter));
            _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
            _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
            _pnlTracker = pnlTracker ?? throw new ArgumentNullException(nameof(pnlTracker));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger;

            // Initialize collections with thread-safe alternatives
            _tickHistory = new ConcurrentDictionary<Symbol, ConcurrentQueue<Tick>>();
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _startupSemaphore = new SemaphoreSlim(0, PipelineConstants.ProcessingThreadCount);
            _tickHistorySemaphore = new SemaphoreSlim(1, 1);
            _shutdownCts = new CancellationTokenSource();

            // Initialize helper classes
            _statisticsCollector = new PipelineStatisticsCollector();
            _strategyProcessor = new ReactiveStrategyProcessor(_strategyEngine, _eventBus, _statisticsCollector, null);
            _componentCoordinator = new ComponentCoordinator(_exchange, _riskManager, _pnlTracker, _eventBus, _statisticsCollector, null);

            // Initialize event handlers with proper async patterns
            InitializeEventHandlers();

            // Start processing task for market data
            _marketDataTask = ProcessMarketDataAsync(_shutdownCts.Token);
        }

        /// <summary>
        /// Start the trading pipeline
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
                _logger?.LogInformation("Starting trading pipeline with capital {Capital:C}", InitialCapital);

                // Start statistics collection
                _statisticsCollector.Start();

                // Start all components in proper order
                await _marketDataProvider.ConnectAsync();
                await _marketDataProvider.SubscribeAsync(symbols);

                _marketDataProcessor.Start();
                _strategyEngine.Start();
                _orderRouter.Start();
                _exchange.Start();

                _isRunning = true;

                // Release semaphore for waiting threads
                for (int i = 0; i < PipelineConstants.ProcessingThreadCount; i++)
                {
                    _startupSemaphore.Release();
                }

                await _eventBus.PublishAsync(new SystemEvent("INFO", "Trading pipeline started", "Pipeline"));
                RaisePipelineEvent("Pipeline Started", PipelineEventType.Started);

                _logger?.LogInformation("Trading pipeline started successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start trading pipeline");
                await StopAsync(); // Cleanup on failure
                throw;
            }
        }

        /// <summary>
        /// Stop the trading pipeline
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
                _logger?.LogInformation("Stopping trading pipeline");

                _isRunning = false;
                _statisticsCollector.Stop();

                // Stop components in reverse order
                _strategyEngine.Stop();
                _orderRouter.Stop();
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

                await _eventBus.PublishAsync(new SystemEvent("INFO", "Trading pipeline stopped", "Pipeline"));
                RaisePipelineEvent("Pipeline Stopped", PipelineEventType.Stopped);

                _logger?.LogInformation("Trading pipeline stopped successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping trading pipeline");
                throw;
            }
        }

        /// <summary>
        /// Process market data in dedicated task with proper error handling
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
                        // Process tick through the processor
                        await _marketDataProcessor.SubmitTickAsync(tick, cancellationToken);

                        // Update tick history with thread-safe operations
                        await UpdateTickHistoryAsync(tick);

                        // Update strategy engine
                        var tickHistory = await GetTickHistoryAsync(tick.Symbol);
                        await _strategyEngine.UpdateMarketDataAsync(tick, tickHistory);

                        // Update exchange prices
                        _exchange.UpdateMarketPrice(tick.Symbol, tick.Bid, tick.Ask);

                        // Update P&L tracker
                        await _pnlTracker.UpdateMarketPriceAsync(tick.Symbol, tick.MidPrice);

                        // Publish event
                        await _eventBus.PublishAsync(new TickReceivedEvent(tick), cancellationToken);

                        _statisticsCollector.IncrementTicksProcessed();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing tick for {Symbol}", tick.Symbol);
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
        /// Thread-safe tick history update using SemaphoreSlim
        /// </summary>
        private async Task UpdateTickHistoryAsync(Tick tick)
        {
            await _tickHistorySemaphore.WaitAsync();
            try
            {
                var history = _tickHistory.GetOrAdd(tick.Symbol, _ => new ConcurrentQueue<Tick>());
                history.Enqueue(tick);

                // Trim old ticks if needed
                while (history.Count > TickHistorySize && history.TryDequeue(out _))
                {
                    // Remove oldest tick
                }
            }
            finally
            {
                _tickHistorySemaphore.Release();
            }
        }

        /// <summary>
        /// Get tick history in a thread-safe manner
        /// </summary>
        private async Task<List<Tick>> GetTickHistoryAsync(Symbol symbol)
        {
            await _tickHistorySemaphore.WaitAsync();
            try
            {
                if (_tickHistory.TryGetValue(symbol, out var history))
                {
                    return history.ToArray().ToList();
                }
                return new List<Tick>();
            }
            finally
            {
                _tickHistorySemaphore.Release();
            }
        }

        /// <summary>
        /// Initialize event handlers with proper patterns (no async lambdas)
        /// </summary>
        private void InitializeEventHandlers()
        {
            // Subscribe to risk breaches with sync handler
            _riskManager.RiskBreached += OnRiskBreached;

            // Strategy engine event subscription is now handled by ReactiveStrategyProcessor
            // No empty event handlers anymore!
        }

        /// <summary>
        /// Handle risk breaches synchronously
        /// </summary>
        private void OnRiskBreached(object? sender, RiskBreachEventArgs args)
        {
            try
            {
                _logger?.LogWarning("Risk breach detected: {Description}", args.Breach.Description);

                if (args.RequiresImmediateAction)
                {
                    _logger?.LogCritical("Immediate action required for risk breach");
                    RaisePipelineEvent($"Critical Risk: {args.Breach.Description}", PipelineEventType.RiskBreach);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling risk breach");
            }
        }

        /// <summary>
        /// Get pipeline statistics
        /// </summary>
        public PipelineStatistics GetStatistics()
        {
            var activePositions = _positions.Count(p => p.Value.IsOpen);
            var eventBusStats = _eventBus.GetStatistics();
            return _statisticsCollector.GetStatistics(activePositions, eventBusStats);
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
        /// Proper async dispose pattern
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

                _shutdownCts.Dispose();
                _startupSemaphore.Dispose();
                _tickHistorySemaphore.Dispose();

                // Dispose injected components if they implement IDisposable
                if (_marketDataProcessor is IDisposable disposableProcessor)
                    disposableProcessor.Dispose();
                
                if (_strategyEngine is IDisposable disposableStrategy)
                    disposableStrategy.Dispose();
                
                if (_orderRouter is IDisposable disposableRouter)
                    disposableRouter.Dispose();
                
                _exchange?.Dispose();
                _pnlTracker?.Dispose();

                if (_riskManager is IDisposable disposableRisk)
                    disposableRisk.Dispose();

                if (_eventBus is IDisposable disposableEventBus)
                    disposableEventBus.Dispose();

                _logger?.LogInformation("Trading pipeline disposed successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during pipeline disposal");
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
    /// Pipeline statistics
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