using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Domain.Entities
{
    /// <summary>
    /// Represents an executed trade (a fill of an order)
    /// </summary>
    public class Trade
    {
        public string TradeId { get; }
        public OrderId OrderId { get; }
        public Symbol Symbol { get; }
        public OrderSide Side { get; }
        public Price ExecutionPrice { get; }
        public Quantity ExecutionQuantity { get; }
        public Timestamp ExecutionTime { get; }
        public decimal Commission { get; }
        public string? ExecutionVenue { get; }
        public string? CounterpartyId { get; }
        public decimal NotionalValue => ExecutionPrice.Value * ExecutionQuantity.Value;
        public decimal NetValue => Side == OrderSide.Buy
            ? NotionalValue + Commission
            : NotionalValue - Commission;

        public Trade(
            OrderId orderId,
            Symbol symbol,
            OrderSide side,
            Price executionPrice,
            Quantity executionQuantity,
            decimal commission = 0,
            string? executionVenue = null,
            string? counterpartyId = null)
        {
            TradeId = GenerateTradeId();
            OrderId = orderId;
            Symbol = symbol;
            Side = side;
            ExecutionPrice = executionPrice;
            ExecutionQuantity = executionQuantity;
            ExecutionTime = Timestamp.Now;
            Commission = commission;
            ExecutionVenue = executionVenue ?? "Internal";
            CounterpartyId = counterpartyId;
        }

        private static string GenerateTradeId()
        {
            // Generate a unique trade ID with timestamp prefix for easier sorting
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var guid = Guid.NewGuid().ToString("N")[..8];
            return $"T{timestamp}{guid}";
        }

        /// <summary>
        /// Calculate the P&L impact of this trade relative to a reference price
        /// </summary>
        public decimal CalculatePnL(Price referencePrice)
        {
            var priceDiff = ExecutionPrice.Value - referencePrice.Value;
            var pnl = priceDiff * ExecutionQuantity.Value * Side.Sign();
            return pnl - Commission;
        }

        /// <summary>
        /// Check if this trade matches specific criteria
        /// </summary>
        public bool Matches(Symbol? symbol = null, OrderSide? side = null, DateTime? afterTime = null)
        {
            if (symbol != null && Symbol != symbol.Value)
                return false;

            if (side != null && Side != side.Value)
                return false;

            if (afterTime != null && ExecutionTime.Value < afterTime.Value)
                return false;

            return true;
        }

        public override string ToString()
        {
            return $"Trade {TradeId} | Order {OrderId.ToShortString()} | " +
                   $"{Symbol} {Side} {ExecutionQuantity} @ {ExecutionPrice} | " +
                   $"Notional: {NotionalValue:C} | Commission: {Commission:C} | " +
                   $"Time: {ExecutionTime}";
        }
    }
}