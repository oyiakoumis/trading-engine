using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.MarketData.Processors
{
    /// <summary>
    /// Processes and validates market data ticks
    /// Maintains tick history and provides data quality checks
    /// </summary>
    public class MarketDataProcessor : IDisposable
    {
        private readonly Channel<Tick> _inputChannel;
        private readonly Channel<Tick> _outputChannel;
        private readonly ConcurrentDictionary<Symbol, CircularBuffer<Tick>> _tickHistory;
        private readonly ConcurrentDictionary<Symbol, TickStatistics> _statistics;
        private readonly int _historySize;
        private readonly int _staleThresholdMs;
        private CancellationTokenSource? _processingCts;
        private Task? _processingTask;
        private bool _disposed;

        public MarketDataProcessor(int historySize = 1000, int staleThresholdMs = 5000)
        {
            _historySize = historySize;
            _staleThresholdMs = staleThresholdMs;
            _tickHistory = new ConcurrentDictionary<Symbol, CircularBuffer<Tick>>();
            _statistics = new ConcurrentDictionary<Symbol, TickStatistics>();

            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            };

            _inputChannel = Channel.CreateUnbounded<Tick>(channelOptions);
            _outputChannel = Channel.CreateUnbounded<Tick>(channelOptions);
        }

        /// <summary>
        /// Start processing market data
        /// </summary>
        public void Start()
        {
            if (_processingTask != null) return;

            _processingCts = new CancellationTokenSource();
            _processingTask = ProcessTicksAsync(_processingCts.Token);
        }

        /// <summary>
        /// Stop processing market data
        /// </summary>
        public async Task StopAsync()
        {
            _processingCts?.Cancel();
            if (_processingTask != null)
            {
                await _processingTask.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Submit a tick for processing
        /// </summary>
        public async ValueTask<bool> SubmitTickAsync(Tick tick, CancellationToken cancellationToken = default)
        {
            if (_disposed) return false;

            try
            {
                await _inputChannel.Writer.WriteAsync(tick, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Get processed ticks
        /// </summary>
        public async IAsyncEnumerable<Tick> GetProcessedTicksAsync(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await _outputChannel.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (_outputChannel.Reader.TryRead(out var tick))
                    {
                        yield return tick;
                    }
                }
            }
        }

        /// <summary>
        /// Get tick history for a symbol
        /// </summary>
        public IReadOnlyList<Tick> GetTickHistory(Symbol symbol)
        {
            if (_tickHistory.TryGetValue(symbol, out var buffer))
            {
                return buffer.ToArray();
            }
            return Array.Empty<Tick>();
        }

        /// <summary>
        /// Get statistics for a symbol
        /// </summary>
        public TickStatistics? GetStatistics(Symbol symbol)
        {
            return _statistics.TryGetValue(symbol, out var stats) ? stats : null;
        }

        private async Task ProcessTicksAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (await _inputChannel.Reader.WaitToReadAsync(cancellationToken))
                    {
                        while (_inputChannel.Reader.TryRead(out var tick))
                        {
                            if (ValidateTick(tick))
                            {
                                // Update history
                                var buffer = _tickHistory.GetOrAdd(tick.Symbol, _ => new CircularBuffer<Tick>(_historySize));
                                buffer.Add(tick);

                                // Update statistics
                                UpdateStatistics(tick);

                                // Forward to output channel
                                await _outputChannel.Writer.WriteAsync(tick, cancellationToken);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log error in production
                    Console.WriteLine($"Error processing tick: {ex.Message}");
                }
            }
        }

        private bool ValidateTick(Tick tick)
        {
            // Check if tick is valid
            if (!tick.IsValid)
            {
                Console.WriteLine($"Invalid tick rejected: {tick.Symbol}");
                return false;
            }

            // Check if tick is stale
            if (tick.IsStale(_staleThresholdMs))
            {
                Console.WriteLine($"Stale tick rejected: {tick.Symbol} (Age: {tick.Age.TotalMilliseconds}ms)");
                return false;
            }

            // Check for negative spread
            if (tick.Spread.Value < 0)
            {
                Console.WriteLine($"Negative spread tick rejected: {tick.Symbol}");
                return false;
            }

            return true;
        }

        private void UpdateStatistics(Tick tick)
        {
            _statistics.AddOrUpdate(tick.Symbol,
                _ => new TickStatistics(tick),
                (_, existing) => existing.Update(tick));
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _processingCts?.Cancel();
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
            _processingCts?.Dispose();
            _inputChannel.Writer.TryComplete();
            _outputChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Circular buffer for efficient tick history storage
    /// </summary>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly object _lock = new();
        private int _head;
        private int _tail;
        private int _count;

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                _buffer[_tail] = item;
                _tail = (_tail + 1) % _buffer.Length;

                if (_count < _buffer.Length)
                {
                    _count++;
                }
                else
                {
                    _head = (_head + 1) % _buffer.Length;
                }
            }
        }

        public T[] ToArray()
        {
            lock (_lock)
            {
                var result = new T[_count];
                if (_count == 0) return result;

                if (_head < _tail)
                {
                    Array.Copy(_buffer, _head, result, 0, _count);
                }
                else
                {
                    var firstPart = _buffer.Length - _head;
                    Array.Copy(_buffer, _head, result, 0, firstPart);
                    Array.Copy(_buffer, 0, result, firstPart, _tail);
                }

                return result;
            }
        }

        public int Count => _count;
    }

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

        public decimal AverageSpread { get; private set; }
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

                // Update spread average
                AverageSpread = ((AverageSpread * (_tickCount - 1)) + tick.Spread.Value) / _tickCount;

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