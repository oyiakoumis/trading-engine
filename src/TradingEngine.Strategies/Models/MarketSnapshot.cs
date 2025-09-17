using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Strategies.Models
{
    /// <summary>
    /// Represents a snapshot of market data for strategy evaluation
    /// </summary>
    public class MarketSnapshot
    {
        public Tick CurrentTick { get; }
        public IReadOnlyList<Tick> RecentTicks { get; }
        public Dictionary<string, object> Indicators { get; }
        public Timestamp Timestamp { get; }

        public MarketSnapshot(
            Tick currentTick,
            IReadOnlyList<Tick> recentTicks,
            Dictionary<string, object>? indicators = null)
        {
            CurrentTick = currentTick;
            RecentTicks = recentTicks ?? new List<Tick>();
            Indicators = indicators ?? new Dictionary<string, object>();
            Timestamp = Timestamp.Now;
        }

        /// <summary>
        /// Get an indicator value
        /// </summary>
        public T? GetIndicator<T>(string name) where T : class
        {
            return Indicators.TryGetValue(name, out var value) ? value as T : null;
        }

        /// <summary>
        /// Calculate simple moving average from recent ticks
        /// </summary>
        public Price? CalculateSMA(int periods)
        {
            if (RecentTicks.Count < periods) return null;

            decimal sum = 0;
            for (int i = RecentTicks.Count - periods; i < RecentTicks.Count; i++)
            {
                sum += RecentTicks[i].MidPrice.Value;
            }

            return new Price(sum / periods);
        }

        /// <summary>
        /// Calculate momentum (price change over period)
        /// </summary>
        public decimal? CalculateMomentum(int periods)
        {
            if (RecentTicks.Count < periods + 1) return null;

            var currentPrice = CurrentTick.MidPrice.Value;
            var pastPrice = RecentTicks[RecentTicks.Count - periods - 1].MidPrice.Value;

            if (pastPrice == 0) return null;

            return ((currentPrice - pastPrice) / pastPrice) * 100;
        }

        /// <summary>
        /// Calculate volatility (standard deviation of returns)
        /// </summary>
        public decimal? CalculateVolatility(int periods)
        {
            if (RecentTicks.Count < periods + 1) return null;

            var returns = new List<decimal>();
            for (int i = RecentTicks.Count - periods; i < RecentTicks.Count; i++)
            {
                var currentPrice = RecentTicks[i].MidPrice.Value;
                var previousPrice = RecentTicks[i - 1].MidPrice.Value;

                if (previousPrice > 0)
                {
                    returns.Add((currentPrice - previousPrice) / previousPrice);
                }
            }

            if (returns.Count == 0) return null;

            // Calculate mean
            decimal mean = 0;
            foreach (var ret in returns)
            {
                mean += ret;
            }
            mean /= returns.Count;

            // Calculate variance
            decimal variance = 0;
            foreach (var ret in returns)
            {
                variance += (ret - mean) * (ret - mean);
            }
            variance /= returns.Count;

            // Return standard deviation
            return (decimal)Math.Sqrt((double)variance);
        }
    }
}