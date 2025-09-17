using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Execution.Interfaces
{
    /// <summary>
    /// Interface for order management
    /// </summary>
    public interface IOrderManager
    {
        /// <summary>
        /// Place a new order
        /// </summary>
        Task<Order> PlaceOrderAsync(
            Symbol symbol,
            OrderSide side,
            OrderType type,
            Quantity quantity,
            Price? limitPrice = null,
            Price? stopPrice = null,
            string? clientId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancel an existing order
        /// </summary>
        Task<bool> CancelOrderAsync(OrderId orderId, string reason = "User requested", CancellationToken cancellationToken = default);

        /// <summary>
        /// Modify an existing order
        /// </summary>
        Task<bool> ModifyOrderAsync(
            OrderId orderId,
            Quantity? newQuantity = null,
            Price? newLimitPrice = null,
            Price? newStopPrice = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Get order by ID
        /// </summary>
        Task<Order?> GetOrderAsync(OrderId orderId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get all active orders
        /// </summary>
        Task<IEnumerable<Order>> GetActiveOrdersAsync(Symbol? symbol = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get order history
        /// </summary>
        Task<IEnumerable<Order>> GetOrderHistoryAsync(Symbol? symbol = null, int limit = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get order statistics
        /// </summary>
        OrderStatistics GetStatistics();

        /// <summary>
        /// Event raised when order status changes
        /// </summary>
        event EventHandler<OrderStatusChangedEventArgs>? OrderStatusChanged;
    }

    /// <summary>
    /// Order status change event args
    /// </summary>
    public class OrderStatusChangedEventArgs : EventArgs
    {
        public Order Order { get; }
        public OrderStatus OldStatus { get; }
        public OrderStatus NewStatus { get; }
        public string? Reason { get; }
        public Timestamp Timestamp { get; }

        public OrderStatusChangedEventArgs(Order order, OrderStatus oldStatus, OrderStatus newStatus, string? reason = null)
        {
            Order = order;
            OldStatus = oldStatus;
            NewStatus = newStatus;
            Reason = reason;
            Timestamp = Timestamp.Now;
        }
    }

    /// <summary>
    /// Order statistics
    /// </summary>
    public class OrderStatistics
    {
        public int TotalOrders { get; set; }
        public int ActiveOrders { get; set; }
        public int FilledOrders { get; set; }
        public int CancelledOrders { get; set; }
        public int RejectedOrders { get; set; }
        public decimal FillRate => TotalOrders > 0 ? (decimal)FilledOrders / TotalOrders : 0;
        public decimal CancellationRate => TotalOrders > 0 ? (decimal)CancelledOrders / TotalOrders : 0;
        public TimeSpan AverageFillTime { get; set; }
    }
}