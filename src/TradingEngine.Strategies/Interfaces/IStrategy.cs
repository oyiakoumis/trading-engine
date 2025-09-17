using TradingEngine.Strategies.Models;

namespace TradingEngine.Strategies.Interfaces
{
    /// <summary>
    /// Base interface for trading strategies
    /// </summary>
    public interface IStrategy
    {
        /// <summary>
        /// Strategy name
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Evaluate market conditions and generate trading signal
        /// </summary>
        Task<Signal?> EvaluateAsync(MarketSnapshot snapshot, PositionContext positionContext);

        /// <summary>
        /// Update strategy parameters
        /// </summary>
        void UpdateParameters(StrategyParameters parameters);

        /// <summary>
        /// Initialize strategy
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Reset strategy state
        /// </summary>
        void Reset();

        /// <summary>
        /// Check if strategy is enabled
        /// </summary>
        bool IsEnabled { get; set; }
    }
}