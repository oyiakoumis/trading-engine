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
    public class OrderRouter : IOrderRouter
    {
        private readonly IOrderManager _orderManager;
        private readonly IExchange _exchange;
        private readonly ConcurrentQueue<Signal> _signalQueue;
        private readonly ConcurrentDictionary<OrderId, Signal> _orderToSignalMap;
        private readonly SemaphoreSlim _routingSemaphore;
        private readonly Timer _processTimer;
        private readonly Timer _cleanupTimer;
        private readonly Queue<DateTime> _recentOrderTimes;
        private readonly object _rateLimitLock = new();
        private bool _isRunning;
        private bool _disposed;

        // Configuration
        public bool EnablePreTradeRiskChecks { get; set; } = true;
        public decimal MaxOrderValue { get; set; } = 100000m;
        public int MaxOrdersPerMinute { get; set; } = 100;
        public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // Statistics - using Interlocked for thread safety
        private int _ordersRoutedCount;
        private int _ordersRejectedCount;
        private long _lastOrderTimeTicks = DateTime.UtcNow.Ticks;
        private readonly object _statisticsLock = new();

        public event EventHandler<OrderRoutedEventArgs>? OrderRouted;
        public event EventHandler<OrderRejectedEventArgs>? OrderRejected;

        public OrderRouter(IOrderManager orderManager, IExchange exchange)
        {
            _orderManager = orderManager ?? throw new ArgumentNullException(nameof(orderManager));
            _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
            _signalQueue = new ConcurrentQueue<Signal>();
            _orderToSignalMap = new ConcurrentDictionary<OrderId, Signal>();
            _routingSemaphore = new SemaphoreSlim(1, 1);
            _recentOrderTimes = new Queue<DateTime>();

            // Process signals every 50ms - using safe synchronous wrapper
            _processTimer = new Timer(
                ProcessSignalsSafely,
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );

            // Cleanup timer for stale signals every 5 minutes
            _cleanupTimer = new Timer(
                CleanupStaleSignals,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5)
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
        /// Safe timer callback wrapper that handles exceptions
        /// </summary>
        private void ProcessSignalsSafely(object? state)
        {
            if (_disposed || !_isRunning) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessSignalsAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Silently handle exceptions to prevent timer from stopping
                    // In production, this should log the exception
                }
            });
        }

        /// <summary>
        /// Process queued signals
        /// </summary>
        private async Task ProcessSignalsAsync()
        {
            if (!_isRunning) return;

            await _routingSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                while (_signalQueue.TryDequeue(out var signal))
                {
                    await RouteSignalAsync(signal).ConfigureAwait(false);
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
                ).ConfigureAwait(false);

                // Map order to signal for tracking
                _orderToSignalMap.TryAdd(order.Id, signal);

                // Submit to exchange
                var submitted = await _exchange.SubmitOrderAsync(order).ConfigureAwait(false);

                if (submitted)
                {
                    Interlocked.Increment(ref _ordersRoutedCount);
                    Interlocked.Exchange(ref _lastOrderTimeTicks, DateTime.UtcNow.Ticks);
                    TrackOrderForRateLimit(); // Track for rate limiting

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
                    await CreateBracketOrdersAsync(order, signal).ConfigureAwait(false);
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
                ).ConfigureAwait(false);

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
                ).ConfigureAwait(false);
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

            // Check order rate limit (sliding window)
            if (IsRateLimited())
            {
                return "Order rate limit exceeded";
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
        /// Check if rate limiting is active using sliding window
        /// </summary>
        private bool IsRateLimited()
        {
            lock (_rateLimitLock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-1);

                // Remove old entries
                while (_recentOrderTimes.Count > 0 && _recentOrderTimes.Peek() < cutoff)
                {
                    _recentOrderTimes.Dequeue();
                }

                return _recentOrderTimes.Count >= MaxOrdersPerMinute;
            }
        }

        /// <summary>
        /// Track order for rate limiting
        /// </summary>
        private void TrackOrderForRateLimit()
        {
            lock (_rateLimitLock)
            {
                _recentOrderTimes.Enqueue(DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Cleanup stale signals from the signal map
        /// </summary>
        private void CleanupStaleSignals(object? state)
        {
            if (_disposed || !_isRunning) return;

            try
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-10); // Remove signals older than 10 minutes
                var staleSignals = _orderToSignalMap
                    .Where(kvp => kvp.Value.GeneratedAt.Value < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var orderId in staleSignals)
                {
                    _orderToSignalMap.TryRemove(orderId, out _);
                }
            }
            catch (Exception)
            {
                // Silently handle exceptions to prevent cleanup from stopping
                // In production, this should log the exception
            }
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
            // Remove from signal map when order reaches final state (filled, cancelled, rejected)
            if (report.Status == OrderStatus.Filled ||
                report.Status == OrderStatus.Cancelled ||
                report.Status == OrderStatus.Rejected)
            {
                _orderToSignalMap.TryRemove(report.OrderId, out _);
            }
        }

        /// <summary>
        /// Get routing statistics (thread-safe)
        /// </summary>
        public OrderRoutingStatistics GetStatistics()
        {
            // Read volatile fields and calculate statistics atomically
            var routed = _ordersRoutedCount;
            var rejected = _ordersRejectedCount;

            return new OrderRoutingStatistics
            {
                OrdersRouted = routed,
                OrdersRejected = rejected,
                ActiveSignals = _signalQueue.Count,
                SuccessRate = (routed + rejected) > 0
                    ? (decimal)routed / (routed + rejected)
                    : 0
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Stop();
            _processTimer?.Dispose();
            _cleanupTimer?.Dispose();
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