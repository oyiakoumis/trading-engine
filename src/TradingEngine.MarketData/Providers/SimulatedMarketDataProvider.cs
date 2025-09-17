using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.MarketData.Interfaces;

namespace TradingEngine.MarketData.Providers
{
    /// <summary>
    /// Simulated market data provider for testing and demonstration
    /// Generates realistic tick data with configurable parameters
    /// </summary>
    public class SimulatedMarketDataProvider : IMarketDataProvider, IDisposable
    {
        private readonly Random _random = new();
        private readonly ConcurrentDictionary<Symbol, TickSimulator> _simulators = new();
        private readonly ConcurrentDictionary<Symbol, Tick> _latestTicks = new();
        private readonly Channel<Tick> _tickChannel;
        private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _generationTask;
        private bool _isConnected;
        private volatile bool _disposed;

        public bool IsConnected => _isConnected;
        public event EventHandler<bool>? ConnectionStatusChanged;

        public SimulatedMarketDataProvider()
        {
            var options = new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };
            _tickChannel = Channel.CreateUnbounded<Tick>(options);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _connectionSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (_isConnected) return;

                _cancellationTokenSource = new CancellationTokenSource();
                _isConnected = true;
                ConnectionStatusChanged?.Invoke(this, true);

                // Start tick generation for all subscribed symbols
                _generationTask = Task.Run(() => GenerateTicksAsync(_cancellationTokenSource.Token), cancellationToken);
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await _connectionSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (!_isConnected) return;

                _cancellationTokenSource?.Cancel();
                if (_generationTask != null)
                {
                    await _generationTask.ConfigureAwait(false);
                }

                _isConnected = false;
                ConnectionStatusChanged?.Invoke(this, false);
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task SubscribeAsync(IEnumerable<Symbol> symbols, CancellationToken cancellationToken = default)
        {
            foreach (var symbol in symbols)
            {
                if (!_simulators.ContainsKey(symbol))
                {
                    var simulator = new TickSimulator(symbol, _random);
                    _simulators.TryAdd(symbol, simulator);

                    // Generate initial tick
                    var initialTick = simulator.GenerateTick();
                    _latestTicks.AddOrUpdate(symbol, initialTick, (_, _) => initialTick);
                }
            }

            await Task.CompletedTask;
        }

        public async Task UnsubscribeAsync(IEnumerable<Symbol> symbols, CancellationToken cancellationToken = default)
        {
            foreach (var symbol in symbols)
            {
                _simulators.TryRemove(symbol, out _);
                _latestTicks.TryRemove(symbol, out _);
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<Tick> StreamTicksAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await _tickChannel.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (_tickChannel.Reader.TryRead(out var tick))
                    {
                        yield return tick;
                    }
                }
            }
        }

        public async Task<Tick?> GetLatestTickAsync(Symbol symbol, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            return _latestTicks.TryGetValue(symbol, out var tick) ? tick : null;
        }

        public async Task<IDictionary<Symbol, Tick>> GetMarketSnapshotAsync(CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            return new Dictionary<Symbol, Tick>(_latestTicks);
        }

        private async Task GenerateTicksAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    foreach (var kvp in _simulators)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        var symbol = kvp.Key;
                        var simulator = kvp.Value;
                        var tick = simulator.GenerateTick();

                        _latestTicks.AddOrUpdate(symbol, tick, (_, _) => tick);

                        if (!_tickChannel.Writer.TryWrite(tick))
                        {
                            await _tickChannel.Writer.WriteAsync(tick, cancellationToken);
                        }
                    }

                    // Simulate market data rate (adjustable)
                    var delay = _random.Next(50, 200); // 50-200ms between tick batches
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log error in production
                    Console.WriteLine($"Error generating ticks: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _cancellationTokenSource?.Cancel();
            _generationTask?.Wait(TimeSpan.FromSeconds(5));
            _cancellationTokenSource?.Dispose();
            _tickChannel.Writer.TryComplete();
            _connectionSemaphore.Dispose();
        }

        /// <summary>
        /// Internal class to simulate tick generation for a specific symbol
        /// </summary>
        private class TickSimulator
        {
            private readonly Symbol _symbol;
            private readonly Random _random;
            private Price _lastBid;
            private Price _lastAsk;
            private Price _lastPrice;
            private Quantity _baseVolume;
            private long _sequenceNumber;
            private readonly decimal _volatility;
            private readonly decimal _spread;

            public TickSimulator(Symbol symbol, Random random)
            {
                _symbol = symbol;
                _random = random;
                _sequenceNumber = 0;

                // Initialize with realistic starting values
                var basePrice = 100m + (decimal)(_random.NextDouble() * 400); // 100-500
                _lastBid = new Price(basePrice - 0.01m);
                _lastAsk = new Price(basePrice + 0.01m);
                _lastPrice = new Price(basePrice);
                _baseVolume = new Quantity(_random.Next(100, 1000));

                // Set volatility and spread based on symbol (could be configured)
                _volatility = 0.002m + (decimal)(_random.NextDouble() * 0.003); // 0.2% - 0.5%
                _spread = 0.02m + (decimal)(_random.NextDouble() * 0.03); // 0.02 - 0.05
            }

            public Tick GenerateTick()
            {
                // Generate price movement
                var priceChange = (decimal)((_random.NextDouble() - 0.5) * 2 * (double)_volatility);
                var midPrice = (_lastBid.Value + _lastAsk.Value) / 2;
                var newMidPrice = midPrice * (1 + priceChange);

                // Ensure minimum price
                if (newMidPrice < 0.01m) newMidPrice = 0.01m;

                // Calculate new bid/ask with dynamic spread
                var spreadMultiplier = 0.5m + (decimal)(_random.NextDouble() * 1.5); // 0.5x - 2x normal spread
                var currentSpread = _spread * spreadMultiplier;

                _lastBid = new Price(newMidPrice - currentSpread / 2);
                _lastAsk = new Price(newMidPrice + currentSpread / 2);

                // Simulate last traded price (usually closer to bid or ask)
                var lastPriceBias = _random.NextDouble();
                if (lastPriceBias < 0.45)
                    _lastPrice = _lastBid;
                else if (lastPriceBias > 0.55)
                    _lastPrice = _lastAsk;
                else
                    _lastPrice = new Price(newMidPrice);

                // Generate volume with some randomness
                var volumeMultiplier = (decimal)(0.5 + _random.NextDouble() * 2); // 0.5x - 2.5x base volume
                var bidSize = new Quantity(_baseVolume.Value * volumeMultiplier);
                var askSize = new Quantity(_baseVolume.Value * volumeMultiplier * (decimal)(0.8 + _random.NextDouble() * 0.4));
                var lastSize = new Quantity(_random.Next(1, (int)Math.Max(1, _baseVolume.Value / 10)));

                return new Tick(
                    _symbol,
                    _lastBid,
                    _lastAsk,
                    bidSize,
                    askSize,
                    Timestamp.Now,
                    _lastPrice,
                    lastSize,
                    ++_sequenceNumber
                );
            }
        }
    }
}