using System.Collections.Concurrent;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Strategies.Interfaces;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Strategies.Engine
{
    /// <summary>
    /// Orchestrates strategy execution and signal generation
    /// Thread-safe and designed for concurrent execution
    /// </summary>
    public class StrategyEngine : IDisposable
    {
        private readonly ConcurrentDictionary<string, IStrategy> _strategies;
        private readonly ConcurrentDictionary<Symbol, MarketSnapshot> _marketSnapshots;
        private readonly ConcurrentDictionary<Symbol, Position> _positions;
        private readonly ConcurrentQueue<Signal> _signalQueue;
        private readonly SemaphoreSlim _executionSemaphore;
        private readonly Timer _periodicEvaluationTimer;
        private readonly object _capitalLock = new();
        private decimal _availableCapital;
        private bool _isRunning;
        private bool _disposed;

        public event EventHandler<Signal>? SignalGenerated;
        public event EventHandler<string>? StrategyError;

        public StrategyEngine(decimal initialCapital = 100000m)
        {
            _strategies = new ConcurrentDictionary<string, IStrategy>();
            _marketSnapshots = new ConcurrentDictionary<Symbol, MarketSnapshot>();
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _signalQueue = new ConcurrentQueue<Signal>();
            _executionSemaphore = new SemaphoreSlim(1, 1);
            _availableCapital = initialCapital;
            _isRunning = false;

            // Set up periodic evaluation timer (every second)
            _periodicEvaluationTimer = new Timer(
                async _ => await EvaluateStrategiesAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        /// <summary>
        /// Register a strategy
        /// </summary>
        public async Task RegisterStrategyAsync(IStrategy strategy)
        {
            await _executionSemaphore.WaitAsync();
            try
            {
                await strategy.InitializeAsync();
                _strategies.TryAdd(strategy.Name, strategy);
            }
            finally
            {
                _executionSemaphore.Release();
            }
        }

        /// <summary>
        /// Unregister a strategy
        /// </summary>
        public bool UnregisterStrategy(string strategyName)
        {
            return _strategies.TryRemove(strategyName, out _);
        }

        /// <summary>
        /// Update market snapshot for a symbol
        /// </summary>
        public async Task UpdateMarketDataAsync(Tick tick, List<Tick> recentTicks)
        {
            var snapshot = new MarketSnapshot(tick, recentTicks);
            _marketSnapshots.AddOrUpdate(tick.Symbol, snapshot, (_, _) => snapshot);

            // Trigger immediate evaluation if we have strategies
            if (_strategies.Any() && _isRunning)
            {
                await EvaluateStrategiesForSymbolAsync(tick.Symbol);
            }
        }

        /// <summary>
        /// Update position information
        /// </summary>
        public void UpdatePosition(Position position)
        {
            _positions.AddOrUpdate(position.Symbol, position, (_, _) => position);
        }

        /// <summary>
        /// Update available capital
        /// </summary>
        public void UpdateCapital(decimal capital)
        {
            lock (_capitalLock)
            {
                _availableCapital = capital;
            }
        }

        /// <summary>
        /// Start the strategy engine
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;

            // Start periodic evaluation
            _periodicEvaluationTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Stop the strategy engine
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _periodicEvaluationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Get pending signals
        /// </summary>
        public IEnumerable<Signal> GetPendingSignals()
        {
            var signals = new List<Signal>();
            while (_signalQueue.TryDequeue(out var signal))
            {
                signals.Add(signal);
            }
            return signals;
        }

        /// <summary>
        /// Evaluate all strategies for all symbols
        /// </summary>
        private async Task EvaluateStrategiesAsync()
        {
            if (!_isRunning) return;

            var tasks = _marketSnapshots.Keys.Select(symbol => EvaluateStrategiesForSymbolAsync(symbol));
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Evaluate strategies for a specific symbol
        /// </summary>
        private async Task EvaluateStrategiesForSymbolAsync(Symbol symbol)
        {
            if (!_marketSnapshots.TryGetValue(symbol, out var snapshot))
                return;

            var positionContext = BuildPositionContext(symbol);

            foreach (var strategy in _strategies.Values.Where(s => s.IsEnabled))
            {
                try
                {
                    var signal = await strategy.EvaluateAsync(snapshot, positionContext);

                    if (signal != null && signal.IsValid())
                    {
                        _signalQueue.Enqueue(signal);
                        SignalGenerated?.Invoke(this, signal);
                    }
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Strategy {strategy.Name} error for {symbol}: {ex.Message}";
                    StrategyError?.Invoke(this, errorMsg);
                }
            }
        }

        /// <summary>
        /// Build position context for strategy evaluation
        /// </summary>
        private PositionContext BuildPositionContext(Symbol symbol)
        {
            _positions.TryGetValue(symbol, out var currentPosition);

            // Calculate P&L
            var realizedPnL = _positions.Values.Sum(p => p.RealizedPnL);
            var unrealizedPnL = 0m;

            foreach (var position in _positions.Values.Where(p => p.IsOpen))
            {
                if (_marketSnapshots.TryGetValue(position.Symbol, out var snapshot))
                {
                    unrealizedPnL += position.CalculateUnrealizedPnL(snapshot.CurrentTick.MidPrice);
                }
            }

            // Build risk metrics
            var riskMetrics = new RiskMetrics
            {
                CurrentDrawdown = CalculateDrawdown(),
                WinRate = CalculateWinRate()
            };

            decimal availableCapital;
            lock (_capitalLock)
            {
                availableCapital = _availableCapital;
            }

            return new PositionContext(
                currentPosition,
                new Dictionary<Symbol, Position>(_positions),
                availableCapital,
                realizedPnL,
                unrealizedPnL,
                0, // Order count would come from OMS
                riskMetrics
            );
        }

        /// <summary>
        /// Calculate current drawdown
        /// </summary>
        private decimal CalculateDrawdown()
        {
            var totalPnL = _positions.Values.Sum(p => p.RealizedPnL);
            if (totalPnL >= 0) return 0;

            return Math.Abs(totalPnL / _availableCapital);
        }

        /// <summary>
        /// Calculate win rate from closed positions
        /// </summary>
        private decimal CalculateWinRate()
        {
            var closedPositions = _positions.Values.Where(p => p.IsClosed).ToList();
            if (!closedPositions.Any()) return 0.5m;

            var wins = closedPositions.Count(p => p.RealizedPnL > 0);
            return (decimal)wins / closedPositions.Count;
        }

        /// <summary>
        /// Get strategy performance metrics
        /// </summary>
        public StrategyPerformance GetPerformance(string strategyName)
        {
            // This would track performance per strategy
            return new StrategyPerformance
            {
                StrategyName = strategyName,
                TotalSignals = 0, // Would need to track this
                WinningSignals = 0,
                LosingSignals = 0,
                TotalPnL = _positions.Values.Sum(p => p.RealizedPnL),
                AverageConfidence = 0.7,
                LastSignalTime = Timestamp.Now
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Stop();
            _periodicEvaluationTimer?.Dispose();
            _executionSemaphore?.Dispose();
        }
    }

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