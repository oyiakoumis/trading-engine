namespace TradingEngine.Domain.Enums
{
    /// <summary>
    /// Represents the lifecycle status of an order
    /// </summary>
    public enum OrderStatus
    {
        /// <summary>
        /// Order has been created but not yet submitted
        /// </summary>
        Pending,

        /// <summary>
        /// Order has been submitted to the exchange
        /// </summary>
        Submitted,

        /// <summary>
        /// Order has been acknowledged by the exchange
        /// </summary>
        Accepted,

        /// <summary>
        /// Order is partially filled
        /// </summary>
        PartiallyFilled,

        /// <summary>
        /// Order is completely filled
        /// </summary>
        Filled,

        /// <summary>
        /// Order has been cancelled
        /// </summary>
        Cancelled,

        /// <summary>
        /// Order has been rejected
        /// </summary>
        Rejected,

        /// <summary>
        /// Order has expired
        /// </summary>
        Expired
    }

    public static class OrderStatusExtensions
    {
        public static bool IsActive(this OrderStatus status)
            => status == OrderStatus.Pending ||
               status == OrderStatus.Submitted ||
               status == OrderStatus.Accepted ||
               status == OrderStatus.PartiallyFilled;

        public static bool IsFinal(this OrderStatus status)
            => status == OrderStatus.Filled ||
               status == OrderStatus.Cancelled ||
               status == OrderStatus.Rejected ||
               status == OrderStatus.Expired;

        public static bool CanCancel(this OrderStatus status)
            => status == OrderStatus.Pending ||
               status == OrderStatus.Submitted ||
               status == OrderStatus.Accepted ||
               status == OrderStatus.PartiallyFilled;

        public static bool CanModify(this OrderStatus status)
            => status == OrderStatus.Pending ||
               status == OrderStatus.Submitted ||
               status == OrderStatus.Accepted;
    }
}