namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Constants used throughout the trading pipeline
    /// </summary>
    public static class PipelineConstants
    {
        // Threading constants
        public const int ProcessingThreadCount = 5;
        public const int MarketDataProcessingDelayMs = 10;
        public const int StrategyProcessingDelayMs = 100;
        public const int OrderProcessingDelayMs = 100;
        public const int RiskProcessingDelayMs = 1000;
        public const int EventProcessingDelayMs = 1000;

        // Timeout constants
        public const int DisposalTimeoutSeconds = 5;
        public const int ComponentStopTimeoutSeconds = 10;

        // Queue sizes
        public const int DefaultChannelCapacity = 10000;

        // History management
        public const int DefaultTickHistorySize = 100;
    }
}