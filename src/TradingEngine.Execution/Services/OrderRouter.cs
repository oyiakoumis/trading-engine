using System.Collections.Concurrent;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Exchange;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Execution.Services
{
    /// <summary>
    /// Routes orders from strategies to execution venues
    /// Handles order validation, risk checks, and routing logic
    /// </summary>
    public class OrderRouter : IDisposable
    {
        private readonly IOrderManager _orderManager;
        private readonly MockExchange _exchange;
        private readonly ConcurrentQueue<Signal> _signalQueue;
        private readonly ConcurrentDictionary<OrderId, Signal> _orderToSignalMap;
        private readonly SemaphoreSlim _routingSemaphore;
        private readonly Timer _processTimer;
        private bool _isRunning;
        private bool _disposed;

        // Configuration
        public bool EnablePreTradeRiskChecks { get; set; } = true;
        public decimal MaxOrderValue { get; set; } = 100000m;
        public int MaxOrdersPerMinute { get; set; } = 100;
        public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // Statistics
        private int _ordersRoutedCount;
        private int _ordersRejectedCount;
        private DateTime _lastOrderTime = DateTime.UtcNow;

        public event EventHandler<OrderRoutedEventArgs>? OrderRouted;
        public event EventHandler<OrderRejectedEventArgs>? OrderRejected;

        public OrderRouter(IOrderManager orderManager, MockExchange exchange)
        {
            _orderManager = orderManager;
            _exchange = exchange;
            _signalQueue = new ConcurrentQueue<Signal>();
            _orderToSignalMap = new ConcurrentDictionary<OrderId, Signal>();
            _routingSemaphore = new SemaphoreSlim(1, 1);

            // Process signals every 50ms
            _processTimer = new Timer(
                async _ => await ProcessSignalsAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );

            // Subscribe to exchange events
            _exchange.ExecutionReported += OnExecutionReported;
        }

        /// <summary>
        /// Start the order router
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            _processTimer.Change(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        }

        /// <summary>
        /// Stop the order router
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _processTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Submit a signal for routing
        /// </summary>
        public void SubmitSignal(Signal signal)
        {
            if (signal.IsValid())
            {
                _signalQueue.Enqueue(signal);
            }
        }

        /// <summary>
        /// Process queued signals
        /// </summary>
        private async Task ProcessSignalsAsync()
        {
            if (!_isRunning) return;

            await _routingSemaphore.WaitAsync();
            try
            {
                while (_signalQueue.TryDequeue(out var signal))
                {
                    await RouteSignalAsync(signal);
                }
            }
            finally
            {
                _routingSemaphore.Release();
            }
        }

        /// <summary>
        /// Route a signal to create an order
        /// </summary>
        private async Task RouteSignalAsync(Signal signal)
        {
            try
            {
                // Pre-trade risk checks
                if (EnablePreTradeRiskChecks)
                {
                    var rejection = PerformPreTradeRiskChecks(signal);
                    if (rejection != null)
                    {
                        RejectSignal(signal, rejection);
                        return;
                    }
                }

                // Determine order type based on signal
                var orderType = signal.TargetPrice.HasValue ? OrderType.Limit : OrderType.Market;

                // Create order from signal
                var order = await _orderManager.PlaceOrderAsync(
                    signal.Symbol,
                    signal.Side,
                    orderType,
                    signal.Quantity,
                    signal.TargetPrice,
                    signal.StopLoss,
                    $"Signal_{signal.GeneratedAt.UnixMilliseconds}"
                );

                // Map order to signal for tracking
                _orderToSignalMap.TryAdd(order.Id, signal);

                // Submit to exchange
                var submitted = await _exchange.SubmitOrderAsync(order);

                if (submitted)
                {
                    Interlocked.Increment(ref _ordersRoutedCount);
                    _lastOrderTime = DateTime.UtcNow;

                    OrderRouted?.Invoke(this, new OrderRoutedEventArgs
                    {
                        Order = order,
                        Signal = signal,
                        RoutedAt = Timestamp.Now
                    });
                }
                else
                {
                    Interlocked.Increment(ref _ordersRejectedCount);
                    RejectSignal(signal, "Exchange rejected order");
                }

                // Handle take profit and stop loss orders
                if (signal.TakeProfit.HasValue || signal.StopLoss.HasValue)
                {
                    await CreateBracketOrdersAsync(order, signal);
                }
            }
            catch (Exception ex)
            {
                RejectSignal(signal, $"Routing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Create bracket orders (take profit and stop loss)
        /// </summary>
        private async Task CreateBracketOrdersAsync(Order parentOrder, Signal signal)
        {
            // Create take profit order
            if (signal.TakeProfit.HasValue)
            {
                var tpSide = signal.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                var tpOrder = await _orderManager.PlaceOrderAsync(
                    signal.Symbol,
                    tpSide,
                    OrderType.Limit,
                    signal.Quantity,
                    signal.TakeProfit,
                    null,
                    $"TP_{parentOrder.Id.ToShortString()}"
                );

                // Note: In a real system, this would be a conditional order
                // that only activates when the parent order is filled
            }

            // Create stop loss order
            if (signal.StopLoss.HasValue)
            {
                var slSide = signal.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
                var slOrder = await _orderManager.PlaceOrderAsync(
                    signal.Symbol,
                    slSide,
                    OrderType.Stop,
                    signal.Quantity,
                    null,
                    signal.StopLoss,
                    $"SL_{parentOrder.Id.ToShortString()}"
                );
            }
        }

        /// <summary>
        /// Perform pre-trade risk checks
        /// </summary>
        private string? PerformPreTradeRiskChecks(Signal signal)
        {
            // Check order value
            if (signal.TargetPrice.HasValue)
            {
                var orderValue = signal.Quantity.Value * signal.TargetPrice.Value.Value;
                if (orderValue > MaxOrderValue)
                {
                    return $"Order value {orderValue:C} exceeds limit {MaxOrderValue:C}";
                }
            }

            // Check order rate limit
            var recentOrders = _ordersRoutedCount; // Simplified - should track per minute
            if (recentOrders > MaxOrdersPerMinute)
            {
                return $"Order rate limit exceeded: {recentOrders} orders per minute";
            }

            // Check signal confidence
            if (signal.Confidence < 0.5)
            {
                return $"Signal confidence {signal.Confidence:P} below threshold";
            }

            // Check for duplicate signals
            var recentSignalExists = _orderToSignalMap.Values
                .Any(s => s.Symbol == signal.Symbol &&
                         s.Side == signal.Side &&
                         (signal.GeneratedAt.Value - s.GeneratedAt.Value).TotalSeconds < 5);

            if (recentSignalExists)
            {
                return "Duplicate signal detected within 5 seconds";
            }

            return null; // Passed all checks
        }

        /// <summary>
        /// Reject a signal
        /// </summary>
        private void RejectSignal(Signal signal, string reason)
        {
            Interlocked.Increment(ref _ordersRejectedCount);

            OrderRejected?.Invoke(this, new OrderRejectedEventArgs
            {
                Signal = signal,
                Reason = reason,
                RejectedAt = Timestamp.Now
            });
        }

        /// <summary>
        /// Handle execution reports from exchange
        /// </summary>
        private void OnExecutionReported(object? sender, ExecutionReport report)
        {
            // Update statistics based on execution reports
            if (report.Status == OrderStatus.Filled)
            {
                // Remove from signal map when fully filled
                _orderToSignalMap.TryRemove(report.OrderId, out _);
            }
        }

        /// <summary>
        /// Get routing statistics
        /// </summary>
        public OrderRoutingStatistics GetStatistics()
        {
            return new OrderRoutingStatistics
            {
                OrdersRouted = _ordersRoutedCount,
                OrdersRejected = _ordersRejectedCount,
                ActiveSignals = _signalQueue.Count,
                SuccessRate = _ordersRoutedCount > 0
                    ? (decimal)(_ordersRoutedCount - _ordersRejectedCount) / _ordersRoutedCount
                    : 0
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Stop();
            _processTimer?.Dispose();
            _routingSemaphore?.Dispose();

            if (_exchange != null)
            {
                _exchange.ExecutionReported -= OnExecutionReported;
            }
        }
    }

    /// <summary>
    /// Order routed event args
    /// </summary>
    public class OrderRoutedEventArgs : EventArgs
    {
        public Order Order { get; set; } = null!;
        public Signal Signal { get; set; } = null!;
        public Timestamp RoutedAt { get; set; }
    }

    /// <summary>
    /// Order rejected event args
    /// </summary>
    public class OrderRejectedEventArgs : EventArgs
    {
        public Signal Signal { get; set; } = null!;
        public string Reason { get; set; } = string.Empty;
        public Timestamp RejectedAt { get; set; }
    }

    /// <summary>
    /// Order routing statistics
    /// </summary>
    public class OrderRoutingStatistics
    {
        public int OrdersRouted { get; set; }
        public int OrdersRejected { get; set; }
        public int ActiveSignals { get; set; }
        public decimal SuccessRate { get; set; }

        public override string ToString()
        {
            return $"Routed: {OrdersRouted}, Rejected: {OrdersRejected}, " +
                   $"Active: {ActiveSignals}, Success: {SuccessRate:P}";
        }
    }
}