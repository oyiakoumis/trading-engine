using System.Collections.Concurrent;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Interfaces;

namespace TradingEngine.Execution.Services
{
    /// <summary>
    /// Manages order lifecycle and maintains order book
    /// Thread-safe implementation with concurrent collections
    /// </summary>
    public class OrderManager : IOrderManager, IDisposable
    {
        private readonly ConcurrentDictionary<OrderId, Order> _orders;
        private readonly ConcurrentDictionary<Symbol, ConcurrentBag<Order>> _ordersBySymbol;
        private readonly ConcurrentQueue<Order> _orderHistory;
        private readonly SemaphoreSlim _orderSemaphore;
        private readonly Timer _cleanupTimer;
        private readonly object _statsLock = new();
        private OrderStatistics _statistics;
        private bool _disposed;

        public event EventHandler<OrderStatusChangedEventArgs>? OrderStatusChanged;

        public OrderManager()
        {
            _orders = new ConcurrentDictionary<OrderId, Order>();
            _ordersBySymbol = new ConcurrentDictionary<Symbol, ConcurrentBag<Order>>();
            _orderHistory = new ConcurrentQueue<Order>();
            _orderSemaphore = new SemaphoreSlim(1, 1);
            _statistics = new OrderStatistics();

            // Cleanup old orders every minute
            _cleanupTimer = new Timer(
                _ => CleanupOldOrders(),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)
            );
        }

        public async Task<Order> PlaceOrderAsync(
            Symbol symbol,
            OrderSide side,
            OrderType type,
            Quantity quantity,
            Price? limitPrice = null,
            Price? stopPrice = null,
            string? clientId = null,
            CancellationToken cancellationToken = default)
        {
            await _orderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Create new order
                var order = new Order(symbol, side, type, quantity, limitPrice, stopPrice, clientId);

                // Add to collections
                if (!_orders.TryAdd(order.Id, order))
                {
                    throw new InvalidOperationException($"Failed to add order {order.Id}");
                }

                _ordersBySymbol.AddOrUpdate(
                    symbol,
                    new ConcurrentBag<Order> { order },
                    (_, bag) =>
                    {
                        bag.Add(order);
                        return bag;
                    }
                );

                // Update statistics
                UpdateStatistics(stats =>
                {
                    stats.TotalOrders++;
                    stats.ActiveOrders++;
                });

                // Raise event
                RaiseOrderStatusChanged(order, OrderStatus.Pending, order.Status);

                return order;
            }
            finally
            {
                _orderSemaphore.Release();
            }
        }

        public async Task<bool> CancelOrderAsync(OrderId orderId, string reason = "User requested", CancellationToken cancellationToken = default)
        {
            if (!_orders.TryGetValue(orderId, out var order))
                return false;

            var oldStatus = order.Status;

            try
            {
                order.Cancel(reason);

                UpdateStatistics(stats =>
                {
                    stats.ActiveOrders--;
                    stats.CancelledOrders++;
                });

                RaiseOrderStatusChanged(order, oldStatus, order.Status, reason);

                await Task.CompletedTask.ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> ModifyOrderAsync(
            OrderId orderId,
            Quantity? newQuantity = null,
            Price? newLimitPrice = null,
            Price? newStopPrice = null,
            CancellationToken cancellationToken = default)
        {
            if (!_orders.TryGetValue(orderId, out var order))
                return false;

            try
            {
                order.Modify(newQuantity, newLimitPrice, newStopPrice);
                await Task.CompletedTask.ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<Order?> GetOrderAsync(OrderId orderId, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return _orders.TryGetValue(orderId, out var order) ? order : null;
        }

        public async Task<IEnumerable<Order>> GetActiveOrdersAsync(Symbol? symbol = null, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            IEnumerable<Order> orders = _orders.Values.Where(o => o.IsActive);

            if (symbol != null)
            {
                orders = orders.Where(o => o.Symbol == symbol.Value);
            }

            return orders.ToList();
        }

        public async Task<IEnumerable<Order>> GetOrderHistoryAsync(Symbol? symbol = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            var history = _orderHistory.ToArray();

            if (symbol != null)
            {
                history = history.Where(o => o.Symbol == symbol.Value).ToArray();
            }

            return history.Take(limit);
        }

        public OrderStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new OrderStatistics
                {
                    TotalOrders = _statistics.TotalOrders,
                    ActiveOrders = _statistics.ActiveOrders,
                    FilledOrders = _statistics.FilledOrders,
                    CancelledOrders = _statistics.CancelledOrders,
                    RejectedOrders = _statistics.RejectedOrders,
                    AverageFillTime = _statistics.AverageFillTime
                };
            }
        }

        /// <summary>
        /// Process order submission to exchange
        /// </summary>
        internal void SubmitOrder(OrderId orderId)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                var oldStatus = order.Status;
                order.Submit();
                RaiseOrderStatusChanged(order, oldStatus, order.Status);
            }
        }

        /// <summary>
        /// Process order acceptance by exchange
        /// </summary>
        internal void AcceptOrder(OrderId orderId)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                var oldStatus = order.Status;
                order.Accept();
                RaiseOrderStatusChanged(order, oldStatus, order.Status);
            }
        }

        /// <summary>
        /// Process order rejection
        /// </summary>
        internal void RejectOrder(OrderId orderId, string reason)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                var oldStatus = order.Status;
                order.Reject(reason);

                UpdateStatistics(stats =>
                {
                    stats.ActiveOrders--;
                    stats.RejectedOrders++;
                });

                RaiseOrderStatusChanged(order, oldStatus, order.Status, reason);
                MoveToHistory(order);
            }
        }

        /// <summary>
        /// Process order fill
        /// </summary>
        internal void FillOrder(OrderId orderId, Quantity fillQuantity, Price fillPrice, decimal commission = 0)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                var oldStatus = order.Status;
                order.Fill(fillQuantity, fillPrice, commission);

                if (order.Status == OrderStatus.Filled)
                {
                    UpdateStatistics(stats =>
                    {
                        stats.ActiveOrders--;
                        stats.FilledOrders++;

                        // Update average fill time
                        if (order.SubmittedAt.HasValue)
                        {
                            var fillTime = Timestamp.Now.Value - order.SubmittedAt.Value.Value;
                            var totalFillTime = stats.AverageFillTime.TotalMilliseconds * (stats.FilledOrders - 1);
                            stats.AverageFillTime = TimeSpan.FromMilliseconds(
                                (totalFillTime + fillTime.TotalMilliseconds) / stats.FilledOrders
                            );
                        }
                    });

                    MoveToHistory(order);
                }

                RaiseOrderStatusChanged(order, oldStatus, order.Status);
            }
        }

        private void MoveToHistory(Order order)
        {
            _orderHistory.Enqueue(order);

            // Keep only last 10000 orders in history
            while (_orderHistory.Count > 10000)
            {
                _orderHistory.TryDequeue(out _);
            }
        }

        private void CleanupOldOrders()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddHours(-24);
                var ordersToRemove = _orders.Values
                    .Where(o => o.IsComplete && o.UpdatedAt?.Value < cutoffTime)
                    .Select(o => o.Id)
                    .ToList();

                foreach (var orderId in ordersToRemove)
                {
                    _orders.TryRemove(orderId, out _);
                }

                // Clean up symbol index - rebuild bags to remove completed orders
                var symbolsToCleanup = _ordersBySymbol.Keys.ToList();
                foreach (var symbol in symbolsToCleanup)
                {
                    if (_ordersBySymbol.TryGetValue(symbol, out var oldBag))
                    {
                        var activeOrders = oldBag.Where(o => !o.IsComplete || o.UpdatedAt?.Value >= cutoffTime);
                        if (activeOrders.Any())
                        {
                            var newBag = new ConcurrentBag<Order>();
                            foreach (var activeOrder in activeOrders)
                            {
                                newBag.Add(activeOrder);
                            }
                            _ordersBySymbol.TryUpdate(symbol, newBag, oldBag);
                        }
                        else
                        {
                            _ordersBySymbol.TryRemove(symbol, out _);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Proper exception handling - don't let cleanup failures break the service
                // In production, this should use proper logging framework
                try
                {
                    Console.WriteLine($"Error cleaning up old orders: {ex.Message}");
                    // Could also log to file or send to monitoring system
                }
                catch
                {
                    // Even logging failed - silently continue to keep service running
                }
            }
        }

        private void UpdateStatistics(Action<OrderStatistics> updateAction)
        {
            lock (_statsLock)
            {
                updateAction(_statistics);
            }
        }

        private void RaiseOrderStatusChanged(Order order, OrderStatus oldStatus, OrderStatus newStatus, string? reason = null)
        {
            OrderStatusChanged?.Invoke(this, new OrderStatusChangedEventArgs(order, oldStatus, newStatus, reason));
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _cleanupTimer?.Dispose();
            _orderSemaphore?.Dispose();
        }
    }
}