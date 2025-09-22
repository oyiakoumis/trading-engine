using TradingEngine.Strategies.Models;

namespace TradingEngine.Strategies.Interfaces
{
    /// <summary>
    /// Interface for tracking strategy performance metrics
    /// </summary>
    public interface IPerformanceTracker
    {
        StrategyPerformance GetPerformance(string strategyName);
        void RecordSignal(string strategyName, Signal signal);
        void RecordTradeResult(string strategyName, decimal pnl);
        void Reset(string strategyName);
    }
}