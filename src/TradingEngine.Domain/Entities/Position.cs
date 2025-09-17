using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Domain.Entities
{
    /// <summary>
    /// Represents a trading position (aggregate root)
    /// Tracks quantity, average price, and P&L
    /// </summary>
    public class Position
    {
        private readonly List<Trade> _trades = new();
        private readonly object _lock = new();

        public string PositionId { get; }
        public Symbol Symbol { get; }
        public Quantity NetQuantity { get; private set; }
        public Quantity LongQuantity { get; private set; }
        public Quantity ShortQuantity { get; private set; }
        public Price AverageEntryPrice { get; private set; }
        public decimal RealizedPnL { get; private set; }
        public decimal TotalCommission { get; private set; }
        public Timestamp OpenedAt { get; private set; }
        public Timestamp? ClosedAt { get; private set; }
        public Timestamp LastUpdatedAt { get; private set; }
        public IReadOnlyList<Trade> Trades => _trades.AsReadOnly();

        public Position(Symbol symbol)
        {
            PositionId = GeneratePositionId();
            Symbol = symbol;
            NetQuantity = Quantity.Zero;
            LongQuantity = Quantity.Zero;
            ShortQuantity = Quantity.Zero;
            AverageEntryPrice = Price.Zero;
            RealizedPnL = 0;
            TotalCommission = 0;
            OpenedAt = Timestamp.Now;
            LastUpdatedAt = Timestamp.Now;
        }

        private static string GeneratePositionId()
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var guid = Guid.NewGuid().ToString("N")[..8];
            return $"P{timestamp}{guid}";
        }

        /// <summary>
        /// Add a trade to the position and update metrics
        /// </summary>
        public void AddTrade(Trade trade)
        {
            if (trade.Symbol != Symbol)
                throw new ArgumentException($"Trade symbol {trade.Symbol} doesn't match position symbol {Symbol}");

            lock (_lock)
            {
                _trades.Add(trade);

                // Update commission
                TotalCommission += trade.Commission;

                // Calculate position changes
                var oldNetQuantity = NetQuantity.Value;
                var tradeQuantitySigned = trade.ExecutionQuantity.Value * trade.Side.Sign();
                var newNetQuantity = oldNetQuantity + tradeQuantitySigned;

                // Check if this is a closing trade (reduces position)
                if (Math.Sign(oldNetQuantity) != 0 && Math.Sign(oldNetQuantity) != Math.Sign(newNetQuantity))
                {
                    // Position is being reduced or closed
                    var closedQuantity = Math.Min(Math.Abs(oldNetQuantity), Math.Abs(tradeQuantitySigned));
                    var pnl = closedQuantity * (trade.ExecutionPrice.Value - AverageEntryPrice.Value) * Math.Sign(oldNetQuantity);
                    RealizedPnL += pnl - trade.Commission;

                    // If position is completely closed
                    if (Math.Abs(newNetQuantity) < 0.0001m)
                    {
                        AverageEntryPrice = Price.Zero;
                        ClosedAt = Timestamp.Now;
                    }
                    // If position is reversed
                    else if (Math.Sign(oldNetQuantity) != Math.Sign(newNetQuantity))
                    {
                        AverageEntryPrice = trade.ExecutionPrice;
                    }
                }
                else if (Math.Sign(newNetQuantity) == Math.Sign(tradeQuantitySigned))
                {
                    // Position is being increased
                    if (AverageEntryPrice.Value == 0)
                    {
                        AverageEntryPrice = trade.ExecutionPrice;
                    }
                    else
                    {
                        // Calculate weighted average entry price
                        var totalValue = AverageEntryPrice.Value * Math.Abs(oldNetQuantity);
                        var tradeValue = trade.ExecutionPrice.Value * trade.ExecutionQuantity.Value;
                        var newTotalQuantity = Math.Abs(oldNetQuantity) + trade.ExecutionQuantity.Value;

                        if (newTotalQuantity > 0)
                        {
                            AverageEntryPrice = new Price(
                                (totalValue + tradeValue) / newTotalQuantity,
                                trade.ExecutionPrice.Precision
                            );
                        }
                    }
                }

                // Update quantities
                NetQuantity = new Quantity(newNetQuantity);

                if (trade.Side == OrderSide.Buy)
                    LongQuantity = LongQuantity + trade.ExecutionQuantity;
                else
                    ShortQuantity = ShortQuantity + trade.ExecutionQuantity;

                LastUpdatedAt = Timestamp.Now;
            }
        }

        /// <summary>
        /// Calculate unrealized P&L based on current market price
        /// </summary>
        public decimal CalculateUnrealizedPnL(Price currentPrice)
        {
            if (NetQuantity.IsZero)
                return 0;

            return NetQuantity.Value * (currentPrice.Value - AverageEntryPrice.Value);
        }

        /// <summary>
        /// Calculate total P&L (realized + unrealized)
        /// </summary>
        public decimal CalculateTotalPnL(Price currentPrice)
        {
            return RealizedPnL + CalculateUnrealizedPnL(currentPrice);
        }

        /// <summary>
        /// Get position side (Long, Short, or Flat)
        /// </summary>
        public string GetPositionSide()
        {
            if (NetQuantity.IsZero)
                return "Flat";
            return NetQuantity.Value > 0 ? "Long" : "Short";
        }

        /// <summary>
        /// Check if position is open
        /// </summary>
        public bool IsOpen => !NetQuantity.IsZero;

        /// <summary>
        /// Check if position is closed
        /// </summary>
        public bool IsClosed => NetQuantity.IsZero && _trades.Any();

        /// <summary>
        /// Get position exposure (notional value)
        /// </summary>
        public decimal GetExposure(Price currentPrice)
        {
            return Math.Abs(NetQuantity.Value * currentPrice.Value);
        }

        /// <summary>
        /// Get position metrics summary
        /// </summary>
        public PositionMetrics GetMetrics(Price currentPrice)
        {
            return new PositionMetrics
            {
                Symbol = Symbol,
                NetQuantity = NetQuantity,
                AverageEntryPrice = AverageEntryPrice,
                CurrentPrice = currentPrice,
                RealizedPnL = RealizedPnL,
                UnrealizedPnL = CalculateUnrealizedPnL(currentPrice),
                TotalPnL = CalculateTotalPnL(currentPrice),
                TotalCommission = TotalCommission,
                TradeCount = _trades.Count,
                Exposure = GetExposure(currentPrice),
                ReturnPercentage = AverageEntryPrice.Value > 0
                    ? ((currentPrice.Value - AverageEntryPrice.Value) / AverageEntryPrice.Value) * 100
                    : 0
            };
        }

        public override string ToString()
        {
            return $"Position {PositionId} | {Symbol} | " +
                   $"Net: {NetQuantity} @ {AverageEntryPrice} | " +
                   $"Realized P&L: {RealizedPnL:C} | " +
                   $"Trades: {_trades.Count}";
        }
    }

    /// <summary>
    /// Position metrics snapshot
    /// </summary>
    public class PositionMetrics
    {
        public Symbol Symbol { get; init; }
        public Quantity NetQuantity { get; init; }
        public Price AverageEntryPrice { get; init; }
        public Price CurrentPrice { get; init; }
        public decimal RealizedPnL { get; init; }
        public decimal UnrealizedPnL { get; init; }
        public decimal TotalPnL { get; init; }
        public decimal TotalCommission { get; init; }
        public int TradeCount { get; init; }
        public decimal Exposure { get; init; }
        public decimal ReturnPercentage { get; init; }

        public override string ToString()
        {
            return $"{Symbol} | Qty: {NetQuantity} | Entry: {AverageEntryPrice} | Current: {CurrentPrice} | " +
                   $"P&L: {TotalPnL:C} (R: {RealizedPnL:C}, U: {UnrealizedPnL:C}) | " +
                   $"Return: {ReturnPercentage:F2}%";
        }
    }
}