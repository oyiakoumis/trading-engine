using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Strategies.Models
{
    /// <summary>
    /// Provides position and risk context for strategy evaluation
    /// </summary>
    public class PositionContext
    {
        public Position? CurrentPosition { get; }
        public Dictionary<Symbol, Position> AllPositions { get; }
        public decimal TotalExposure { get; }
        public decimal AvailableCapital { get; }
        public decimal RealizedPnL { get; }
        public decimal UnrealizedPnL { get; }
        public int OpenOrderCount { get; }
        public RiskMetrics RiskMetrics { get; }

        public PositionContext(
            Position? currentPosition = null,
            Dictionary<Symbol, Position>? allPositions = null,
            decimal availableCapital = 0,
            decimal realizedPnL = 0,
            decimal unrealizedPnL = 0,
            int openOrderCount = 0,
            RiskMetrics? riskMetrics = null)
        {
            CurrentPosition = currentPosition;
            AllPositions = allPositions ?? new Dictionary<Symbol, Position>();
            TotalExposure = CalculateTotalExposure();
            AvailableCapital = availableCapital;
            RealizedPnL = realizedPnL;
            UnrealizedPnL = unrealizedPnL;
            OpenOrderCount = openOrderCount;
            RiskMetrics = riskMetrics ?? new RiskMetrics();
        }

        /// <summary>
        /// Check if there's an open position for the symbol
        /// </summary>
        public bool HasPosition(Symbol symbol)
        {
            return AllPositions.ContainsKey(symbol) && AllPositions[symbol].IsOpen;
        }

        /// <summary>
        /// Get the net quantity for a symbol
        /// </summary>
        public Quantity GetNetQuantity(Symbol symbol)
        {
            if (AllPositions.TryGetValue(symbol, out var position))
            {
                return position.NetQuantity;
            }
            return Quantity.Zero;
        }

        /// <summary>
        /// Calculate total exposure across all positions
        /// </summary>
        private decimal CalculateTotalExposure()
        {
            decimal total = 0;
            foreach (var position in AllPositions.Values)
            {
                if (position.IsOpen)
                {
                    total += Math.Abs(position.NetQuantity.Value * position.AverageEntryPrice.Value);
                }
            }
            return total;
        }

        /// <summary>
        /// Check if adding a new position would exceed risk limits
        /// </summary>
        public bool WouldExceedRiskLimits(Quantity quantity, Price price)
        {
            var additionalExposure = quantity.Value * price.Value;
            var newTotalExposure = TotalExposure + additionalExposure;

            // Check exposure limit (e.g., max 80% of capital)
            if (newTotalExposure > AvailableCapital * RiskMetrics.MaxExposureRatio)
                return true;

            // Check position count limit
            if (AllPositions.Count >= RiskMetrics.MaxPositionCount)
                return true;

            return false;
        }
    }

    /// <summary>
    /// Risk metrics and limits
    /// </summary>
    public class RiskMetrics
    {
        public decimal MaxExposureRatio { get; set; } = 0.8m; // 80% of capital
        public int MaxPositionCount { get; set; } = 10;
        public decimal MaxPositionSizeRatio { get; set; } = 0.2m; // 20% per position
        public decimal MaxDailyLossLimit { get; set; } = 0.05m; // 5% daily loss limit
        public decimal CurrentDrawdown { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal Sharpe { get; set; }
        public decimal WinRate { get; set; }
    }
}