using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.MarketData.Models
{
    /// <summary>
    /// Statistics for market data
    /// </summary>
    public class TickStatistics
    {
        private readonly object _lock = new();
        private decimal _totalVolume;
        private decimal _totalNotional;
        private int _tickCount;
        private Price _highPrice;
        private Price _lowPrice;
        private Price _openPrice;
        private Price _lastPrice;
        private Timestamp _firstTickTime;
        private Timestamp _lastTickTime;
        private decimal _totalSpread;

        public decimal AverageSpread => _tickCount > 0 ? _totalSpread / _tickCount : 0;
        public decimal AverageVolume => _tickCount > 0 ? _totalVolume / _tickCount : 0;
        public decimal VWAP => _totalVolume > 0 ? _totalNotional / _totalVolume : 0;
        public int TicksPerSecond { get; private set; }
        public Price High => _highPrice;
        public Price Low => _lowPrice;
        public Price Open => _openPrice;
        public Price Last => _lastPrice;
        public int TickCount => _tickCount;

        public TickStatistics(Tick firstTick)
        {
            _highPrice = firstTick.MidPrice;
            _lowPrice = firstTick.MidPrice;
            _openPrice = firstTick.MidPrice;
            _lastPrice = firstTick.MidPrice;
            _firstTickTime = firstTick.Timestamp;
            _lastTickTime = firstTick.Timestamp;
            Update(firstTick);
        }

        public TickStatistics Update(Tick tick)
        {
            lock (_lock)
            {
                _tickCount++;
                _lastPrice = tick.MidPrice;

                if (tick.MidPrice > _highPrice)
                    _highPrice = tick.MidPrice;

                if (tick.MidPrice < _lowPrice)
                    _lowPrice = tick.MidPrice;

                // Update volume and notional
                var avgSize = (tick.BidSize.Value + tick.AskSize.Value) / 2;
                _totalVolume += avgSize;
                _totalNotional += avgSize * tick.MidPrice.Value;

                // Update spread total for optimized average calculation
                _totalSpread += tick.Spread.Value;

                // Calculate ticks per second
                var elapsed = (tick.Timestamp.Value - _firstTickTime.Value).TotalSeconds;
                if (elapsed > 0)
                {
                    TicksPerSecond = (int)(_tickCount / elapsed);
                }

                _lastTickTime = tick.Timestamp;

                return this;
            }
        }
    }
}