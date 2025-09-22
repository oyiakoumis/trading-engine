namespace TradingEngine.Strategies.Engine
{
    /// <summary>
    /// Configuration options for the StrategyEngine
    /// </summary>
    public class StrategyEngineOptions
    {
        public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromSeconds(1);
        public decimal InitialCapital { get; set; } = 100000m;
    }
}