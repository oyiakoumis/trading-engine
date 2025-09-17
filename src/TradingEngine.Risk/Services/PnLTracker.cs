using System.Collections.Concurrent;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Risk.Services
{
    /// <summary>
    /// Tracks profit and loss across positions and portfolio
    /// Provides real-time P&L calculations and performance metrics
    /// </summary>
    public class PnLTracker : IDisposable
    {
        private readonly ConcurrentDictionary<Symbol, Position> _positions;
        private readonly ConcurrentDictionary<Symbol, Price> _marketPrices;
        private readonly ConcurrentDictionary<DateTime, DailyPnL> _dailyPnL;
        private readonly SemaphoreSlim _pnlSemaphore;
        private readonly Timer _calculationTimer;
        private decimal _totalRealizedPnL;
        private decimal _totalUnrealizedPnL;
        private decimal _peakValue;
        private decimal _initialCapital;
        private bool _disposed;

        public event EventHandler<PnLUpdateEventArgs>? PnLUpdated;

        public PnLTracker(decimal initialCapital = 100000m)
        {
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _marketPrices = new ConcurrentDictionary<Symbol, Price>();
            _dailyPnL = new ConcurrentDictionary<DateTime, DailyPnL>();
            _pnlSemaphore = new SemaphoreSlim(1, 1);
            _initialCapital = initialCapital;
            _peakValue = initialCapital;

            // Calculate P&L every second
            _calculationTimer = new Timer(
                async _ => await CalculatePnLAsync(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)
            );
        }

        /// <summary>
        /// Update position after trade
        /// </summary>
        public async Task UpdatePositionAsync(Position position)
        {
            await _pnlSemaphore.WaitAsync();
            try
            {
                _positions.AddOrUpdate(position.Symbol, position, (_, _) => position);

                // Update realized P&L
                _totalRealizedPnL = _positions.Values.Sum(p => p.RealizedPnL);

                // Update daily P&L
                var today = DateTime.UtcNow.Date;
                var dailyPnL = _dailyPnL.GetOrAdd(today, _ => new DailyPnL(today));
                dailyPnL.RealizedPnL = _totalRealizedPnL;

                await RecalculateMetricsAsync();
            }
            finally
            {
                _pnlSemaphore.Release();
            }
        }

        /// <summary>
        /// Update market price for mark-to-market
        /// </summary>
        public async Task UpdateMarketPriceAsync(Symbol symbol, Price price)
        {
            _marketPrices.AddOrUpdate(symbol, price, (_, _) => price);

            if (_positions.ContainsKey(symbol))
            {
                await CalculatePnLAsync();
            }
        }

        /// <summary>
        /// Get current P&L summary
        /// </summary>
        public async Task<PnLSummary> GetPnLSummaryAsync()
        {
            await _pnlSemaphore.WaitAsync();
            try
            {
                return new PnLSummary
                {
                    RealizedPnL = _totalRealizedPnL,
                    UnrealizedPnL = _totalUnrealizedPnL,
                    TotalPnL = _totalRealizedPnL + _totalUnrealizedPnL,
                    DailyPnL = GetTodaysPnL(),
                    WeeklyPnL = GetWeeklyPnL(),
                    MonthlyPnL = GetMonthlyPnL(),
                    ReturnPercent = CalculateReturnPercent(),
                    MaxDrawdown = CalculateMaxDrawdown(),
                    SharpeRatio = CalculateSharpeRatio(),
                    WinRate = CalculateWinRate(),
                    ProfitFactor = CalculateProfitFactor(),
                    PositionCount = _positions.Count(p => p.Value.IsOpen),
                    Timestamp = Timestamp.Now
                };
            }
            finally
            {
                _pnlSemaphore.Release();
            }
        }

        /// <summary>
        /// Get P&L by symbol
        /// </summary>
        public async Task<SymbolPnL> GetSymbolPnLAsync(Symbol symbol)
        {
            await _pnlSemaphore.WaitAsync();
            try
            {
                if (!_positions.TryGetValue(symbol, out var position))
                {
                    return new SymbolPnL { Symbol = symbol };
                }

                var currentPrice = _marketPrices.TryGetValue(symbol, out var price)
                    ? price
                    : position.AverageEntryPrice;

                var unrealizedPnL = position.CalculateUnrealizedPnL(currentPrice);

                return new SymbolPnL
                {
                    Symbol = symbol,
                    Position = position.NetQuantity,
                    AverageEntryPrice = position.AverageEntryPrice,
                    CurrentPrice = currentPrice,
                    RealizedPnL = position.RealizedPnL,
                    UnrealizedPnL = unrealizedPnL,
                    TotalPnL = position.RealizedPnL + unrealizedPnL,
                    ReturnPercent = position.AverageEntryPrice.Value > 0
                        ? ((currentPrice.Value - position.AverageEntryPrice.Value) / position.AverageEntryPrice.Value) * 100
                        : 0,
                    TradeCount = position.Trades.Count
                };
            }
            finally
            {
                _pnlSemaphore.Release();
            }
        }

        /// <summary>
        /// Get historical P&L data
        /// </summary>
        public async Task<IEnumerable<DailyPnL>> GetHistoricalPnLAsync(DateTime startDate, DateTime endDate)
        {
            await Task.CompletedTask;
            return _dailyPnL.Values
                .Where(d => d.Date >= startDate && d.Date <= endDate)
                .OrderBy(d => d.Date);
        }

        private async Task CalculatePnLAsync()
        {
            await _pnlSemaphore.WaitAsync();
            try
            {
                _totalUnrealizedPnL = 0;

                foreach (var position in _positions.Values.Where(p => p.IsOpen))
                {
                    if (_marketPrices.TryGetValue(position.Symbol, out var currentPrice))
                    {
                        _totalUnrealizedPnL += position.CalculateUnrealizedPnL(currentPrice);
                    }
                }

                // Update daily P&L
                var today = DateTime.UtcNow.Date;
                var dailyPnL = _dailyPnL.GetOrAdd(today, _ => new DailyPnL(today));
                dailyPnL.UnrealizedPnL = _totalUnrealizedPnL;
                dailyPnL.TotalPnL = _totalRealizedPnL + _totalUnrealizedPnL;

                // Track peak value for drawdown calculation
                var currentValue = _initialCapital + _totalRealizedPnL + _totalUnrealizedPnL;
                if (currentValue > _peakValue)
                {
                    _peakValue = currentValue;
                }

                // Raise event
                PnLUpdated?.Invoke(this, new PnLUpdateEventArgs
                {
                    RealizedPnL = _totalRealizedPnL,
                    UnrealizedPnL = _totalUnrealizedPnL,
                    TotalPnL = _totalRealizedPnL + _totalUnrealizedPnL,
                    UpdateTime = Timestamp.Now
                });
            }
            finally
            {
                _pnlSemaphore.Release();
            }
        }

        private async Task RecalculateMetricsAsync()
        {
            await CalculatePnLAsync();
        }

        private decimal GetTodaysPnL()
        {
            var today = DateTime.UtcNow.Date;
            return _dailyPnL.TryGetValue(today, out var pnl) ? pnl.TotalPnL : 0;
        }

        private decimal GetWeeklyPnL()
        {
            var weekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
            return _dailyPnL.Values
                .Where(d => d.Date >= weekStart)
                .Sum(d => d.TotalPnL);
        }

        private decimal GetMonthlyPnL()
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            return _dailyPnL.Values
                .Where(d => d.Date >= monthStart)
                .Sum(d => d.TotalPnL);
        }

        private decimal CalculateReturnPercent()
        {
            if (_initialCapital == 0) return 0;
            var totalPnL = _totalRealizedPnL + _totalUnrealizedPnL;
            return (totalPnL / _initialCapital) * 100;
        }

        private decimal CalculateMaxDrawdown()
        {
            var currentValue = _initialCapital + _totalRealizedPnL + _totalUnrealizedPnL;
            if (_peakValue == 0) return 0;
            var drawdown = (_peakValue - currentValue) / _peakValue;
            return Math.Max(0, drawdown);
        }

        private decimal CalculateSharpeRatio()
        {
            // Simplified Sharpe ratio calculation
            var returns = _dailyPnL.Values
                .Select(d => d.TotalPnL / _initialCapital)
                .ToList();

            if (returns.Count < 2) return 0;

            var avgReturn = returns.Average();
            var stdDev = CalculateStandardDeviation(returns);

            if (stdDev == 0) return 0;

            var riskFreeRate = 0.02m / 252; // 2% annual risk-free rate, daily
            return (avgReturn - riskFreeRate) / stdDev * (decimal)Math.Sqrt(252); // Annualized
        }

        private decimal CalculateStandardDeviation(List<decimal> values)
        {
            if (values.Count < 2) return 0;

            var avg = values.Average();
            var sumOfSquares = values.Sum(v => (v - avg) * (v - avg));
            var variance = sumOfSquares / (values.Count - 1);
            return (decimal)Math.Sqrt((double)variance);
        }

        private decimal CalculateWinRate()
        {
            var closedPositions = _positions.Values.Where(p => p.IsClosed).ToList();
            if (!closedPositions.Any()) return 0;

            var winningPositions = closedPositions.Count(p => p.RealizedPnL > 0);
            return (decimal)winningPositions / closedPositions.Count;
        }

        private decimal CalculateProfitFactor()
        {
            var profits = _positions.Values
                .Where(p => p.RealizedPnL > 0)
                .Sum(p => p.RealizedPnL);

            var losses = Math.Abs(_positions.Values
                .Where(p => p.RealizedPnL < 0)
                .Sum(p => p.RealizedPnL));

            return losses > 0 ? profits / losses : profits > 0 ? decimal.MaxValue : 0;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _calculationTimer?.Dispose();
            _pnlSemaphore?.Dispose();
        }
    }

    /// <summary>
    /// P&L summary
    /// </summary>
    public class PnLSummary
    {
        public decimal RealizedPnL { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal TotalPnL { get; set; }
        public decimal DailyPnL { get; set; }
        public decimal WeeklyPnL { get; set; }
        public decimal MonthlyPnL { get; set; }
        public decimal ReturnPercent { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal SharpeRatio { get; set; }
        public decimal WinRate { get; set; }
        public decimal ProfitFactor { get; set; }
        public int PositionCount { get; set; }
        public Timestamp Timestamp { get; set; }

        public override string ToString()
        {
            return $"P&L Summary: Total={TotalPnL:C} (R: {RealizedPnL:C}, U: {UnrealizedPnL:C}) | " +
                   $"Return: {ReturnPercent:F2}% | DD: {MaxDrawdown:P} | " +
                   $"Sharpe: {SharpeRatio:F2} | Win Rate: {WinRate:P}";
        }
    }

    /// <summary>
    /// Symbol-specific P&L
    /// </summary>
    public class SymbolPnL
    {
        public Symbol Symbol { get; set; }
        public Quantity Position { get; set; }
        public Price AverageEntryPrice { get; set; }
        public Price CurrentPrice { get; set; }
        public decimal RealizedPnL { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal TotalPnL { get; set; }
        public decimal ReturnPercent { get; set; }
        public int TradeCount { get; set; }

        public override string ToString()
        {
            return $"{Symbol}: Pos={Position} @ {AverageEntryPrice} | " +
                   $"Current={CurrentPrice} | P&L={TotalPnL:C} ({ReturnPercent:F2}%)";
        }
    }

    /// <summary>
    /// Daily P&L record
    /// </summary>
    public class DailyPnL
    {
        public DateTime Date { get; }
        public decimal RealizedPnL { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal TotalPnL { get; set; }
        public int TradeCount { get; set; }

        public DailyPnL(DateTime date)
        {
            Date = date.Date;
        }
    }

    /// <summary>
    /// P&L update event args
    /// </summary>
    public class PnLUpdateEventArgs : EventArgs
    {
        public decimal RealizedPnL { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal TotalPnL { get; set; }
        public Timestamp UpdateTime { get; set; }
    }
}