using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Risk.Interfaces
{
    /// <summary>
    /// Interface for risk management operations
    /// </summary>
    public interface IRiskManager
    {
        /// <summary>
        /// Perform pre-trade risk check
        /// </summary>
        Task<RiskCheckResult> CheckPreTradeRiskAsync(Order order);

        /// <summary>
        /// Perform post-trade risk check
        /// </summary>
        Task<RiskCheckResult> CheckPostTradeRiskAsync(Trade trade);

        /// <summary>
        /// Update risk limits
        /// </summary>
        void UpdateRiskLimits(RiskLimits limits);

        /// <summary>
        /// Get current risk metrics
        /// </summary>
        Task<RiskMetrics> GetRiskMetricsAsync();

        /// <summary>
        /// Get exposure by symbol
        /// </summary>
        Task<decimal> GetExposureAsync(Symbol symbol);

        /// <summary>
        /// Get total portfolio exposure
        /// </summary>
        Task<decimal> GetTotalExposureAsync();

        /// <summary>
        /// Check if risk limits are breached
        /// </summary>
        Task<IEnumerable<RiskBreach>> CheckRiskBreachesAsync();

        /// <summary>
        /// Event raised when risk limit is breached
        /// </summary>
        event EventHandler<RiskBreachEventArgs>? RiskBreached;
    }

    /// <summary>
    /// Result of risk check
    /// </summary>
    public class RiskCheckResult
    {
        public bool Passed { get; set; }
        public string? RejectionReason { get; set; }
        public RiskLevel RiskLevel { get; set; }
        public Dictionary<string, object> Details { get; set; } = new();

        public static RiskCheckResult Pass(RiskLevel level = RiskLevel.Low)
            => new() { Passed = true, RiskLevel = level };

        public static RiskCheckResult Fail(string reason, RiskLevel level = RiskLevel.High)
            => new() { Passed = false, RejectionReason = reason, RiskLevel = level };
    }

    /// <summary>
    /// Risk level enumeration
    /// </summary>
    public enum RiskLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Risk limits configuration
    /// </summary>
    public class RiskLimits
    {
        public decimal MaxPositionSize { get; set; } = 10000;
        public decimal MaxOrderValue { get; set; } = 100000;
        public decimal MaxDailyLoss { get; set; } = 10000;
        public decimal MaxDrawdown { get; set; } = 0.20m; // 20%
        public decimal MaxExposure { get; set; } = 500000;
        public decimal MaxLeverage { get; set; } = 2.0m;
        public int MaxOpenPositions { get; set; } = 20;
        public int MaxOrdersPerMinute { get; set; } = 100;
        public decimal ConcentrationLimit { get; set; } = 0.30m; // 30% per symbol
    }

    /// <summary>
    /// Current risk metrics
    /// </summary>
    public class RiskMetrics
    {
        public decimal TotalExposure { get; set; }
        public decimal NetExposure { get; set; }
        public decimal GrossExposure { get; set; }
        public decimal CurrentLeverage { get; set; }
        public decimal DailyPnL { get; set; }
        public decimal CurrentDrawdown { get; set; }
        public decimal MaxDrawdown { get; set; }
        public int OpenPositions { get; set; }
        public decimal VaR95 { get; set; } // Value at Risk (95% confidence)
        public decimal Sharpe { get; set; }
        public Dictionary<Symbol, decimal> ExposureBySymbol { get; set; } = new();
        public Timestamp CalculatedAt { get; set; }
    }

    /// <summary>
    /// Risk breach information
    /// </summary>
    public class RiskBreach
    {
        public string RuleId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RiskLevel Severity { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal LimitValue { get; set; }
        public Timestamp DetectedAt { get; set; }
        public string RecommendedAction { get; set; } = string.Empty;
    }

    /// <summary>
    /// Risk breach event args
    /// </summary>
    public class RiskBreachEventArgs : EventArgs
    {
        public RiskBreach Breach { get; }
        public bool RequiresImmediateAction { get; }

        public RiskBreachEventArgs(RiskBreach breach, bool requiresAction)
        {
            Breach = breach;
            RequiresImmediateAction = requiresAction;
        }
    }
}