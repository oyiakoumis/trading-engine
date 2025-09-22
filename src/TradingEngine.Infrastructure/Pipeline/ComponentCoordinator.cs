using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Exchange;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.Risk.Interfaces;
using TradingEngine.Risk.Services;

namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Coordinates component interactions using reactive patterns
    /// </summary>
    public class ComponentCoordinator : IDisposable
    {
        private readonly IExchange _exchange;
        private readonly IRiskManager _riskManager;
        private readonly PnLTracker _pnlTracker;
        private readonly IEventBus _eventBus;
        private readonly PipelineStatisticsCollector _statisticsCollector;
        private readonly ILogger<ComponentCoordinator>? _logger;

        // Reactive channels for async event processing
        private readonly Channel<ExecutionReport> _executionReports;
        private readonly Channel<(Symbol Symbol, Position Position)> _positionUpdates;
        private readonly Channel<(Symbol Symbol, decimal PnL)> _pnlUpdates;

        private readonly CancellationTokenSource _processingCts;
        private readonly Task[] _processingTasks;
        private bool _disposed;

        public ComponentCoordinator(
            IExchange exchange,
            IRiskManager riskManager,
            PnLTracker pnlTracker,
            IEventBus eventBus,
            PipelineStatisticsCollector statisticsCollector,
            ILogger<ComponentCoordinator>? logger = null)
        {
            _exchange = exchange;
            _riskManager = riskManager;
            _pnlTracker = pnlTracker;
            _eventBus = eventBus;
            _statisticsCollector = statisticsCollector;
            _logger = logger;

            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            };

            _executionReports = Channel.CreateUnbounded<ExecutionReport>(channelOptions);
            _positionUpdates = Channel.CreateUnbounded<(Symbol, Position)>(channelOptions);
            _pnlUpdates = Channel.CreateUnbounded<(Symbol, decimal)>(channelOptions);

            _processingCts = new CancellationTokenSource();

            // Start processing tasks
            _processingTasks = new[]
            {
                ProcessExecutionReportsAsync(_processingCts.Token),
                ProcessPositionUpdatesAsync(_processingCts.Token),
                ProcessPnLUpdatesAsync(_processingCts.Token)
            };

            // Subscribe to events
            InitializeEventHandlers();
        }

        private void InitializeEventHandlers()
        {
            // Subscribe to exchange execution reports
            _exchange.ExecutionReported += OnExecutionReported;

            // Subscribe to P&L updates
            _pnlTracker.PnLUpdated += OnPnLUpdated;
        }

        private void OnExecutionReported(object? sender, ExecutionReport report)
        {
            if (!_executionReports.Writer.TryWrite(report))
            {
                _logger?.LogWarning("Failed to queue execution report for {OrderId}", report.OrderId);
            }
        }

        private void OnPnLUpdated(object? sender, PnLUpdateEventArgs args)
        {
            if (!_pnlUpdates.Writer.TryWrite((Symbol.Create("PORTFOLIO"), args.TotalPnL)))
            {
                _logger?.LogWarning("Failed to queue P&L update");
            }
        }

        private async Task ProcessExecutionReportsAsync(CancellationToken cancellationToken)
        {
            _logger?.LogInformation("Execution report processing started");

            try
            {
                await foreach (var report in _executionReports.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Update position tracking if trade occurred
                        if (report.Trade != null)
                        {
                            var position = new Position(report.Symbol);
                            position.AddTrade(report.Trade);

                            // Queue position update
                            if (!_positionUpdates.Writer.TryWrite((report.Symbol, position)))
                            {
                                _logger?.LogWarning("Failed to queue position update for {Symbol}", report.Symbol);
                            }

                            // Publish order executed event
                            await _eventBus.PublishAsync(new OrderExecutedEvent(
                                report.OrderId,
                                report.Trade,
                                report.Status == Domain.Enums.OrderStatus.Filled
                            ), cancellationToken);

                            _statisticsCollector.IncrementOrdersExecuted();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing execution report for {OrderId}", report.OrderId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Execution report processing failed");
            }
            finally
            {
                _logger?.LogInformation("Execution report processing stopped");
            }
        }

        private async Task ProcessPositionUpdatesAsync(CancellationToken cancellationToken)
        {
            _logger?.LogInformation("Position update processing started");

            try
            {
                await foreach (var (symbol, position) in _positionUpdates.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Update P&L tracker
                        await _pnlTracker.UpdatePositionAsync(position);

                        // Update risk manager if it supports position updates
                        if (_riskManager is RiskManager riskManager)
                        {
                            riskManager.UpdatePosition(position);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing position update for {Symbol}", symbol);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Position update processing failed");
            }
            finally
            {
                _logger?.LogInformation("Position update processing stopped");
            }
        }

        private async Task ProcessPnLUpdatesAsync(CancellationToken cancellationToken)
        {
            _logger?.LogInformation("P&L update processing started");

            try
            {
                await foreach (var (symbol, pnl) in _pnlUpdates.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // For now, just log the P&L update
                        // In a full implementation, this could trigger other actions
                        _logger?.LogDebug("P&L updated for {Symbol}: {PnL:C}", symbol, pnl);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing P&L update for {Symbol}", symbol);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "P&L update processing failed");
            }
            finally
            {
                _logger?.LogInformation("P&L update processing stopped");
            }
        }

        public void Stop()
        {
            _processingCts.Cancel();
        }

        public async Task StopAsync()
        {
            Stop();
            try
            {
                await Task.WhenAll(_processingTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            // Unsubscribe from events
            _exchange.ExecutionReported -= OnExecutionReported;
            _pnlTracker.PnLUpdated -= OnPnLUpdated;

            _processingCts.Cancel();
            try
            {
                Task.WhenAll(_processingTasks).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during component coordinator shutdown");
            }

            _processingCts.Dispose();
            _executionReports.Writer.TryComplete();
            _positionUpdates.Writer.TryComplete();
            _pnlUpdates.Writer.TryComplete();
        }
    }
}