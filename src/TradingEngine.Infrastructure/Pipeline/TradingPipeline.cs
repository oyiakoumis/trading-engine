using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Exchange;
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
    /// Main trading pipeline that orchestrates all components
    /// Implements concurrent processing with dedicated threads for each component
    /// </summary>
    public class TradingPipeline : IDisposable
    {
        // Core components
        private readonly IMarketDataProvider _marketDataProvider;
        private readonly MarketDataProcessor _marketDataProcessor;
        private readonly StrategyEngine _strategyEngine;
        private readonly IOrderManager _orderManager;
        private readonly OrderRouter _orderRouter;
        private readonly MockExchange _exchange;
        private readonly IRiskManager _riskManager;
        private readonly PnLTracker _pnlTracker;
        private readonly IEventBus _eventBus;
        private readonly ILogger<TradingPipeline>? _logger;

        // Thread management
        private readonly CancellationTokenSource _shutdownCts;
        private readonly Task _marketDataTask;
        private readonly Task _strategyTask;
        private readonly Task _orderTask;
        private readonly Task _riskTask;
        private readonly Task _eventTask;
        private readonly SemaphoreSlim _startupSemaphore;

        // State management
        private readonly ConcurrentDictionary<Symbol, List<Tick>> _tickHistory;
        private readonly ConcurrentDictionary<Symbol, Position> _positions;
        private bool _isRunning;
        private bool _disposed;

        // Configuration
        public int TickHistorySize { get; set; } = 100;
        public decimal InitialCapital { get; set; } = 100000m;

        // Statistics
        private long _ticksProcessed;
        private long _signalsGenerated;
        private long _ordersExecuted;
        private DateTime _startTime;

        public event EventHandler<PipelineEventArgs>? PipelineEvent;

        public TradingPipeline(
            IMarketDataProvider marketDataProvider,
            StrategyEngine strategyEngine,
            IOrderManager orderManager,
            MockExchange exchange,
            IRiskManager riskManager,
            PnLTracker pnlTracker,
            IEventBus eventBus,
            ILogger<TradingPipeline>? logger = null)
        {
            _marketDataProvider = marketDataProvider;
            _marketDataProcessor = new MarketDataProcessor();
            _strategyEngine = strategyEngine;
            _orderManager = orderManager;
            _exchange = exchange;
            _riskManager = riskManager;
            _pnlTracker = pnlTracker;
            _eventBus = eventBus;
            _logger = logger;

            _orderRouter = new OrderRouter(orderManager, exchange);

            _tickHistory = new ConcurrentDictionary<Symbol, List<Tick>>();
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _startupSemaphore = new SemaphoreSlim(0, 1);
            _shutdownCts = new CancellationTokenSource();

            // Initialize components
            InitializeEventHandlers();

            // Start processing tasks
            _marketDataTask = ProcessMarketDataAsync(_shutdownCts.Token);
            _strategyTask = ProcessStrategiesAsync(_shutdownCts.Token);
            _orderTask = ProcessOrdersAsync(_shutdownCts.Token);
            _riskTask = ProcessRiskAsync(_shutdownCts.Token);
            _eventTask = ProcessEventsAsync(_shutdownCts.Token);
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

            _logger?.LogInformation("Starting trading pipeline with capital {Capital:C}", InitialCapital);
            _startTime = DateTime.UtcNow;

            // Start all components
            await _marketDataProvider.ConnectAsync();
            await _marketDataProvider.SubscribeAsync(symbols);

            _marketDataProcessor.Start();
            _strategyEngine.Start();
            _orderRouter.Start();
            _exchange.Start();

            _isRunning = true;
            _startupSemaphore.Release();

            await _eventBus.PublishAsync(new SystemEvent("INFO", "Trading pipeline started", "Pipeline"));

            RaisePipelineEvent("Pipeline Started", PipelineEventType.Started);

            _logger?.LogInformation("Trading pipeline started successfully");
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

            _logger?.LogInformation("Stopping trading pipeline");

            _isRunning = false;

            // Stop components
            _strategyEngine.Stop();
            _orderRouter.Stop();
            _exchange.Stop();
            await _marketDataProcessor.StopAsync();
            await _marketDataProvider.DisconnectAsync();

            // Cancel processing tasks
            _shutdownCts.Cancel();

            // Wait for tasks to complete
            var allTasks = new[] { _marketDataTask, _strategyTask, _orderTask, _riskTask, _eventTask };
            await Task.WhenAll(allTasks);

            await _eventBus.PublishAsync(new SystemEvent("INFO", "Trading pipeline stopped", "Pipeline"));

            RaisePipelineEvent("Pipeline Stopped", PipelineEventType.Stopped);

            _logger?.LogInformation("Trading pipeline stopped successfully");
        }

        /// <summary>
        /// Process market data in dedicated thread
        /// </summary>
        private async Task ProcessMarketDataAsync(CancellationToken cancellationToken)
        {
            await _startupSemaphore.WaitAsync(cancellationToken);

            _logger?.LogInformation("Market data processing thread started");

            await foreach (var tick in _marketDataProvider.StreamTicksAsync(cancellationToken))
            {
                try
                {
                    // Process tick through the processor
                    await _marketDataProcessor.SubmitTickAsync(tick, cancellationToken);

                    // Update tick history
                    var history = _tickHistory.GetOrAdd(tick.Symbol, _ => new List<Tick>());
                    lock (history)
                    {
                        history.Add(tick);
                        if (history.Count > TickHistorySize)
                            history.RemoveAt(0);
                    }

                    // Update strategy engine
                    await _strategyEngine.UpdateMarketDataAsync(tick, history);

                    // Update exchange prices
                    _exchange.UpdateMarketPrice(tick.Symbol, tick.Bid, tick.Ask);

                    // Update P&L tracker
                    await _pnlTracker.UpdateMarketPriceAsync(tick.Symbol, tick.MidPrice);

                    // Publish event
                    await _eventBus.PublishAsync(new TickReceivedEvent(tick));

                    Interlocked.Increment(ref _ticksProcessed);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing tick for {Symbol}", tick.Symbol);
                }
            }

            _logger?.LogInformation("Market data processing thread stopped");
        }

        /// <summary>
        /// Process strategies in dedicated thread
        /// </summary>
        private async Task ProcessStrategiesAsync(CancellationToken cancellationToken)
        {
            await _startupSemaphore.WaitAsync(cancellationToken);

            _logger?.LogInformation("Strategy processing thread started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Get pending signals from strategy engine
                    var signals = _strategyEngine.GetPendingSignals();

                    foreach (var signal in signals)
                    {
                        // Submit signal to order router
                        _orderRouter.SubmitSignal(signal);

                        // Publish event
                        await _eventBus.PublishAsync(new SignalGeneratedEvent(
                            signal.Symbol,
                            signal.Side,
                            signal.Quantity,
                            signal.Type.ToString(),
                            signal.Confidence,
                            signal.Reason
                        ));

                        Interlocked.Increment(ref _signalsGenerated);
                    }

                    await Task.Delay(100, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing strategies");
                }
            }

            _logger?.LogInformation("Strategy processing thread stopped");
        }

        /// <summary>
        /// Process orders in dedicated thread
        /// </summary>
        private async Task ProcessOrdersAsync(CancellationToken cancellationToken)
        {
            await _startupSemaphore.WaitAsync(cancellationToken);

            _logger?.LogInformation("Order processing thread started");

            // Subscribe to execution reports
            _exchange.ExecutionReported += async (sender, report) =>
            {
                try
                {
                    // Update position tracking
                    if (report.Trade != null)
                    {
                        var position = _positions.GetOrAdd(report.Symbol, _ => new Position(report.Symbol));
                        position.AddTrade(report.Trade);

                        _strategyEngine.UpdatePosition(position);
                        await _pnlTracker.UpdatePositionAsync(position);

                        if (_riskManager is RiskManager rm)
                        {
                            rm.UpdatePosition(position);
                        }

                        await _eventBus.PublishAsync(new OrderExecutedEvent(
                            report.OrderId,
                            report.Trade,
                            report.Status == OrderStatus.Filled
                        ));

                        Interlocked.Increment(ref _ordersExecuted);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing execution report");
                }
            };

            // Monitor order status changes
            if (_orderManager is OrderManager om)
            {
                om.OrderStatusChanged += async (sender, args) =>
                {
                    try
                    {
                        await _eventBus.PublishAsync(new OrderPlacedEvent(args.Order));
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error handling order status change");
                    }
                };
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }

            _logger?.LogInformation("Order processing thread stopped");
        }

        /// <summary>
        /// Process risk checks in dedicated thread
        /// </summary>
        private async Task ProcessRiskAsync(CancellationToken cancellationToken)
        {
            await _startupSemaphore.WaitAsync(cancellationToken);

            _logger?.LogInformation("Risk processing thread started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Check for risk breaches
                    var breaches = await _riskManager.CheckRiskBreachesAsync();

                    foreach (var breach in breaches)
                    {
                        await _eventBus.PublishAsync(new RiskLimitBreachedEvent(
                            breach.RuleId,
                            breach.Description,
                            breach.CurrentValue,
                            breach.LimitValue,
                            breach.RecommendedAction
                        ));

                        if (breach.Severity == Risk.Interfaces.RiskLevel.Critical)
                        {
                            _logger?.LogCritical("Critical risk breach: {Description}", breach.Description);
                            RaisePipelineEvent($"Critical Risk: {breach.Description}", PipelineEventType.RiskBreach);
                        }
                    }

                    await Task.Delay(1000, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing risk checks");
                }
            }

            _logger?.LogInformation("Risk processing thread stopped");
        }

        /// <summary>
        /// Process events in dedicated thread
        /// </summary>
        private async Task ProcessEventsAsync(CancellationToken cancellationToken)
        {
            await _startupSemaphore.WaitAsync(cancellationToken);

            _logger?.LogInformation("Event processing thread started");

            // Log P&L updates
            _pnlTracker.PnLUpdated += async (sender, args) =>
            {
                await _eventBus.PublishAsync(new PnLUpdatedEvent(
                    Symbol.Create("PORTFOLIO"),
                    args.RealizedPnL,
                    args.UnrealizedPnL,
                    args.TotalPnL,
                    args.TotalPnL
                ));
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
            }

            _logger?.LogInformation("Event processing thread stopped");
        }

        /// <summary>
        /// Initialize event handlers
        /// </summary>
        private void InitializeEventHandlers()
        {
            // Subscribe to risk breaches
            _riskManager.RiskBreached += (sender, args) =>
            {
                _logger?.LogWarning("Risk breach detected: {Description}", args.Breach.Description);

                if (args.RequiresImmediateAction)
                {
                    _logger?.LogCritical("Immediate action required for risk breach");
                    // Could halt trading or reduce positions
                }
            };
        }

        /// <summary>
        /// Get pipeline statistics
        /// </summary>
        public PipelineStatistics GetStatistics()
        {
            var uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero;

            return new PipelineStatistics
            {
                IsRunning = _isRunning,
                Uptime = uptime,
                TicksProcessed = _ticksProcessed,
                SignalsGenerated = _signalsGenerated,
                OrdersExecuted = _ordersExecuted,
                ActivePositions = _positions.Count(p => p.Value.IsOpen),
                EventBusStats = _eventBus.GetStatistics()
            };
        }

        private void RaisePipelineEvent(string message, PipelineEventType eventType)
        {
            PipelineEvent?.Invoke(this, new PipelineEventArgs
            {
                Message = message,
                EventType = eventType,
                Timestamp = Timestamp.Now
            });
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (_isRunning)
            {
                StopAsync().GetAwaiter().GetResult();
            }

            _shutdownCts.Dispose();
            _startupSemaphore.Dispose();
            _marketDataProcessor?.Dispose();
            _strategyEngine?.Dispose();
            _orderRouter?.Dispose();
            _exchange?.Dispose();
            _pnlTracker?.Dispose();

            if (_riskManager is IDisposable disposableRisk)
            {
                disposableRisk.Dispose();
            }

            if (_eventBus is IDisposable disposableEventBus)
            {
                disposableEventBus.Dispose();
            }
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