using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Domain.Entities
{
    /// <summary>
    /// Represents a trading order with full lifecycle management
    /// </summary>
    public class Order
    {
        private readonly object _lock = new();

        public OrderId Id { get; }
        public Symbol Symbol { get; }
        public OrderSide Side { get; }
        public OrderType Type { get; }
        public Quantity Quantity { get; private set; }
        public Quantity FilledQuantity { get; private set; }
        public Quantity RemainingQuantity => Quantity - FilledQuantity;
        public Price? LimitPrice { get; private set; }
        public Price? StopPrice { get; private set; }
        public OrderStatus Status { get; private set; }
        public Timestamp CreatedAt { get; }
        public Timestamp? SubmittedAt { get; private set; }
        public Timestamp? UpdatedAt { get; private set; }
        public string? RejectReason { get; private set; }
        public string? ClientId { get; }
        public string? Tag { get; }
        public Price AverageFillPrice { get; private set; }
        public decimal TotalCommission { get; private set; }

        public Order(
            Symbol symbol,
            OrderSide side,
            OrderType type,
            Quantity quantity,
            Price? limitPrice = null,
            Price? stopPrice = null,
            string? clientId = null,
            string? tag = null)
        {
            Id = OrderId.NewId();
            Symbol = symbol;
            Side = side;
            Type = type;
            Quantity = quantity;
            LimitPrice = limitPrice;
            StopPrice = stopPrice;
            ClientId = clientId;
            Tag = tag;
            Status = OrderStatus.Pending;
            CreatedAt = Timestamp.Now;
            FilledQuantity = Quantity.Zero;
            AverageFillPrice = Price.Zero;

            ValidateOrder();
        }

        /// <summary>
        /// Validate order parameters
        /// </summary>
        private void ValidateOrder()
        {
            if (Quantity.IsZero)
                throw new ArgumentException("Order quantity cannot be zero");

            if (Type.RequiresPrice() && (LimitPrice == null || LimitPrice.Value.Value <= 0))
                throw new ArgumentException($"Order type {Type} requires a valid limit price");

            if (Type.RequiresStopPrice() && (StopPrice == null || StopPrice.Value.Value <= 0))
                throw new ArgumentException($"Order type {Type} requires a valid stop price");
        }

        /// <summary>
        /// Submit the order for execution
        /// </summary>
        public void Submit()
        {
            lock (_lock)
            {
                if (Status != OrderStatus.Pending)
                    throw new InvalidOperationException($"Cannot submit order in status {Status}");

                Status = OrderStatus.Submitted;
                SubmittedAt = Timestamp.Now;
                UpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Mark order as accepted by exchange
        /// </summary>
        public void Accept()
        {
            lock (_lock)
            {
                if (Status != OrderStatus.Submitted)
                    throw new InvalidOperationException($"Cannot accept order in status {Status}");

                Status = OrderStatus.Accepted;
                UpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Apply a fill to the order
        /// </summary>
        public void Fill(Quantity fillQuantity, Price fillPrice, decimal commission = 0)
        {
            lock (_lock)
            {
                if (!Status.IsActive())
                    throw new InvalidOperationException($"Cannot fill order in status {Status}");

                if (fillQuantity > RemainingQuantity)
                    throw new ArgumentException($"Fill quantity {fillQuantity} exceeds remaining quantity {RemainingQuantity}");

                // Update average fill price
                var totalFilled = FilledQuantity.Value;
                var totalValue = AverageFillPrice.Value * totalFilled;
                var newValue = fillPrice.Value * fillQuantity.Value;
                var newTotalFilled = totalFilled + fillQuantity.Value;

                if (newTotalFilled > 0)
                {
                    AverageFillPrice = new Price((totalValue + newValue) / newTotalFilled, fillPrice.Precision);
                }

                FilledQuantity = FilledQuantity + fillQuantity;
                TotalCommission += commission;

                Status = FilledQuantity == Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
                UpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Cancel the order
        /// </summary>
        public void Cancel(string? reason = null)
        {
            lock (_lock)
            {
                if (!Status.CanCancel())
                    throw new InvalidOperationException($"Cannot cancel order in status {Status}");

                Status = OrderStatus.Cancelled;
                RejectReason = reason ?? "User requested cancellation";
                UpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Reject the order
        /// </summary>
        public void Reject(string reason)
        {
            lock (_lock)
            {
                if (Status.IsFinal())
                    throw new InvalidOperationException($"Cannot reject order in final status {Status}");

                Status = OrderStatus.Rejected;
                RejectReason = reason;
                UpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Modify order parameters (only for pending/accepted orders)
        /// </summary>
        public void Modify(Quantity? newQuantity = null, Price? newLimitPrice = null, Price? newStopPrice = null)
        {
            lock (_lock)
            {
                if (!Status.CanModify())
                    throw new InvalidOperationException($"Cannot modify order in status {Status}");

                if (newQuantity != null)
                {
                    if (newQuantity.Value <= FilledQuantity)
                        throw new ArgumentException("New quantity must be greater than filled quantity");
                    Quantity = newQuantity.Value;
                }

                if (newLimitPrice != null && Type.RequiresPrice())
                    LimitPrice = newLimitPrice;

                if (newStopPrice != null && Type.RequiresStopPrice())
                    StopPrice = newStopPrice;

                UpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Check if order is complete
        /// </summary>
        public bool IsComplete => Status.IsFinal();

        /// <summary>
        /// Check if order is active
        /// </summary>
        public bool IsActive => Status.IsActive();

        /// <summary>
        /// Calculate the notional value of the order
        /// </summary>
        public decimal NotionalValue => Type switch
        {
            OrderType.Market => 0, // Market orders don't have a defined notional until filled
            OrderType.Limit => (LimitPrice?.Value ?? 0) * Quantity.Value,
            OrderType.Stop => (StopPrice?.Value ?? 0) * Quantity.Value,
            OrderType.StopLimit => (LimitPrice?.Value ?? 0) * Quantity.Value,
            _ => 0
        };

        /// <summary>
        /// Calculate the filled notional value
        /// </summary>
        public decimal FilledNotionalValue => AverageFillPrice.Value * FilledQuantity.Value;

        /// <summary>
        /// Get fill percentage
        /// </summary>
        public decimal FillPercentage => Quantity.Value > 0 ? (FilledQuantity.Value / Quantity.Value) * 100 : 0;

        public override string ToString()
        {
            return $"Order {Id.ToShortString()} | {Symbol} {Side} {Quantity} @ " +
                   $"{(Type == OrderType.Market ? "MKT" : LimitPrice?.ToString() ?? "N/A")} | " +
                   $"Status: {Status} | Filled: {FilledQuantity}/{Quantity} @ {AverageFillPrice}";
        }
    }
}