namespace TradingEngine.Console.Configuration
{
    public class TradingConfiguration
    {
        public decimal InitialCapital { get; set; } = 100000m;
        public int TickHistorySize { get; set; } = 100;
        public int StatisticsUpdateIntervalMs { get; set; } = 5000;
        public string[] Symbols { get; set; } = Array.Empty<string>();
        public StrategyConfiguration Strategy { get; set; } = new();
        public MockExchangeConfiguration MockExchange { get; set; } = new();
    }

    public class StrategyConfiguration
    {
        public MomentumStrategyConfiguration Momentum { get; set; } = new();
    }

    public class MomentumStrategyConfiguration
    {
        public int LookbackPeriod { get; set; } = 20;
        public decimal MomentumThreshold { get; set; } = 2.0m;
        public decimal TakeProfitPercent { get; set; } = 2.0m;
        public decimal StopLossPercent { get; set; } = 1.0m;
        public decimal PositionSizePercent { get; set; } = 10.0m;
        public double MinConfidence { get; set; } = 0.6;
    }

    public class MockExchangeConfiguration
    {
        public int SimulatedLatencyMs { get; set; } = 10;
        public decimal SlippagePercent { get; set; } = 0.01m;
        public double PartialFillProbability { get; set; } = 0.2;
        public double RejectProbability { get; set; } = 0.02;
    }
}