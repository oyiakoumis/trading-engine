using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Strategies.Models
{
    /// <summary>
    /// Strategy performance metrics
    /// </summary>
    public class StrategyPerformance
    {
        public string StrategyName { get; set; } = string.Empty;
        public int TotalSignals { get; set; }
        public int WinningSignals { get; set; }
        public int LosingSignals { get; set; }
        public decimal TotalPnL { get; set; }
        public double AverageConfidence { get; set; }
        public Timestamp LastSignalTime { get; set; }

        public decimal WinRate => TotalSignals > 0 ? (decimal)WinningSignals / TotalSignals : 0;
        public decimal AveragePnL => TotalSignals > 0 ? TotalPnL / TotalSignals : 0;

        public override string ToString()
        {
            return $"{StrategyName}: Signals={TotalSignals}, WinRate={WinRate:P}, " +
                   $"PnL={TotalPnL:C}, AvgConfidence={AverageConfidence:P}";
        }
    }
}