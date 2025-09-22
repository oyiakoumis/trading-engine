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
        private readonly StrategyEngineOptions _options;

        private decimal _availableCapital;
        private volatile bool _isRunning;
        private volatile bool _disposed;

        public event EventHandler<Signal>? SignalGenerated;

        public StrategyEngine(decimal initialCapital = 100000m)
            : this(new StrategyEngineOptions { InitialCapital = initialCapital })
        {
        }

        public StrategyEngine(StrategyEngineOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _strategies = new ConcurrentDictionary<string, IStrategy>();
            _marketSnapshots = new ConcurrentDictionary<Symbol, MarketSnapshot>();
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _signalQueue = new ConcurrentQueue<Signal>();
            _executionSemaphore = new SemaphoreSlim(1, 1);
            _availableCapital = _options.InitialCapital;
            _isRunning = false;

            // Set up periodic evaluation timer with safe callback
            _periodicEvaluationTimer = new Timer(
                TimerCallback,
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        /// <summary>
        /// Safe timer callback that handles exceptions
        /// </summary>
        private void TimerCallback(object? state)
        {
            if (_disposed || !_isRunning) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await EvaluateStrategiesAsync();
                }
                catch (Exception)
                {
                    // Silently handle exceptions to prevent timer from stopping
                    // In production, this should log the exception
                }
            });
        }

        /// <summary>
        /// Register a strategy
        /// </summary>
        public async Task RegisterStrategyAsync(IStrategy strategy)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(strategy);

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
        /// Update market snapshot for a symbol with race condition protection
        /// </summary>
        public async Task UpdateMarketDataAsync(Tick tick, List<Tick> recentTicks)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(recentTicks);

            var snapshot = new MarketSnapshot(tick, recentTicks);
            _marketSnapshots.AddOrUpdate(tick.Symbol, snapshot, (_, _) => snapshot);

            // Early exit if not running
            if (!_isRunning) return;

            // Get snapshot of strategies to avoid race conditions
            var strategies = _strategies.Values.ToArray();
            if (strategies.Length > 0)
            {
                await EvaluateStrategiesForSymbolAsync(tick.Symbol);
            }
        }

        /// <summary>
        /// Start the strategy engine
        /// </summary>
        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isRunning) return;

            _isRunning = true;
            _periodicEvaluationTimer.Change(_options.EvaluationInterval, _options.EvaluationInterval);
        }

        /// <summary>
        /// Stop the strategy engine
        /// </summary>
        public void Stop()
        {
            if (_disposed) return;

            _isRunning = false;
            _periodicEvaluationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Get strategy performance metrics (not implemented)
        /// </summary>
        public StrategyPerformance GetPerformance(string strategyName)
        {
            throw new NotImplementedException("Performance tracking is not yet implemented");
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

            // Direct iteration instead of LINQ to avoid allocations
            foreach (var strategy in _strategies.Values)
            {
                if (!strategy.IsEnabled) continue;

                try
                {
                    var signal = await strategy.EvaluateAsync(snapshot, positionContext);

                    if (signal != null && signal.IsValid())
                    {
                        _signalQueue.Enqueue(signal);
                        SignalGenerated?.Invoke(this, signal);
                    }
                }
                catch (Exception)
                {
                    // In production, this should be logged
                    // Silently continue with other strategies
                }
            }
        }

        /// <summary>
        /// Build position context for strategy evaluation with optimized calculations
        /// </summary>
        private PositionContext BuildPositionContext(Symbol symbol)
        {
            _positions.TryGetValue(symbol, out var currentPosition);

            // Single iteration optimization - calculate all metrics in one pass
            decimal realizedPnL = 0;
            decimal unrealizedPnL = 0;
            int closedPositions = 0;
            int winningPositions = 0;

            foreach (var position in _positions.Values)
            {
                realizedPnL += position.RealizedPnL;

                if (position.IsOpen)
                {
                    if (_marketSnapshots.TryGetValue(position.Symbol, out var snapshot))
                    {
                        unrealizedPnL += position.CalculateUnrealizedPnL(snapshot.CurrentTick.MidPrice);
                    }
                }
                else if (position.IsClosed)
                {
                    closedPositions++;
                    if (position.RealizedPnL > 0)
                    {
                        winningPositions++;
                    }
                }
            }

            // Calculate win rate
            var winRate = closedPositions > 0 ? (decimal)winningPositions / closedPositions : 0.5m;

            // Build risk metrics
            var riskMetrics = new RiskMetrics
            {
                CurrentDrawdown = CalculateDrawdown(realizedPnL),
                WinRate = winRate
            };

            decimal availableCapital;
            lock (_capitalLock)
            {
                availableCapital = _availableCapital;
            }

            // Use concurrent dictionary directly to avoid copying
            return new PositionContext(
                currentPosition,
                _positions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                availableCapital,
                realizedPnL,
                unrealizedPnL,
                0, // Order count would come from OMS
                riskMetrics
            );
        }

        /// <summary>
        /// Calculate current drawdown with division by zero protection
        /// </summary>
        private decimal CalculateDrawdown(decimal totalPnL)
        {
            if (totalPnL >= 0) return 0;

            lock (_capitalLock)
            {
                return _availableCapital == 0 ? 0 : Math.Abs(totalPnL / _availableCapital);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            // Stop the engine first
            Stop();

            // Dispose strategies if they implement IDisposable
            foreach (var strategy in _strategies.Values)
            {
                if (strategy is IDisposable disposableStrategy)
                {
                    try
                    {
                        disposableStrategy.Dispose();
                    }
                    catch (Exception)
                    {
                        // Continue disposing other resources
                    }
                }
            }

            // Dispose resources
            _periodicEvaluationTimer?.Dispose();
            _executionSemaphore?.Dispose();
        }
    }
}