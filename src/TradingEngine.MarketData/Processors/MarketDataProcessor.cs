using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.MarketData.DataStructures;
using TradingEngine.MarketData.Interfaces;
using TradingEngine.MarketData.Models;

namespace TradingEngine.MarketData.Processors
{
    /// <summary>
    /// Processes and validates market data ticks
    /// Maintains tick history and provides data quality checks
    /// </summary>
    public class MarketDataProcessor : IMarketDataProcessor
    {
        private readonly Channel<Tick> _inputChannel;
        private readonly ConcurrentDictionary<Symbol, CircularBuffer<Tick>> _tickHistory;
        private readonly ConcurrentDictionary<Symbol, TickStatistics> _statistics;
        private readonly int _historySize;
        private readonly int _staleThresholdMs;
        private readonly ILogger<MarketDataProcessor>? _logger;
        private readonly object _startLock = new();

        private CancellationTokenSource? _processingCts;
        private Task? _processingTask;
        private bool _disposed;

        public MarketDataProcessor(
            ILogger<MarketDataProcessor>? logger = null,
            int historySize = 1000,
            int staleThresholdMs = 5000)
        {
            _logger = logger;
            _historySize = historySize;
            _staleThresholdMs = staleThresholdMs;
            _tickHistory = new ConcurrentDictionary<Symbol, CircularBuffer<Tick>>();
            _statistics = new ConcurrentDictionary<Symbol, TickStatistics>();

            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            };

            _inputChannel = Channel.CreateUnbounded<Tick>(channelOptions);
        }

        /// <summary>
        /// Start processing market data
        /// </summary>
        public void Start()
        {
            lock (_startLock)
            {
                if (_processingTask != null)
                {
                    _logger?.LogWarning("Market data processor is already running");
                    return;
                }

                _processingCts = new CancellationTokenSource();
                _processingTask = ProcessTicksAsync(_processingCts.Token);
                _logger?.LogInformation("Market data processor started");
            }
        }

        /// <summary>
        /// Stop processing market data
        /// </summary>
        public async Task StopAsync()
        {
            _logger?.LogInformation("Stopping market data processor");

            _processingCts?.Cancel();
            if (_processingTask != null)
            {
                try
                {
                    await _processingTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    _logger?.LogWarning("Market data processor did not stop within timeout");
                }
            }

            _logger?.LogInformation("Market data processor stopped");
        }

        /// <summary>
        /// Submit a tick for processing
        /// </summary>
        public async ValueTask<bool> SubmitTickAsync(Tick tick, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                _logger?.LogWarning("Cannot submit tick - processor is disposed");
                return false;
            }

            try
            {
                await _inputChannel.Writer.WriteAsync(tick, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Tick submission cancelled for symbol {Symbol}", tick.Symbol);
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error submitting tick for symbol {Symbol}", tick.Symbol);
                return false;
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
            _logger?.LogDebug("Market data processing task started");

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

                                _logger?.LogTrace("Processed valid tick for symbol {Symbol}", tick.Symbol);
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
                    _logger?.LogError(ex, "Error processing tick batch");
                }
            }

            _logger?.LogDebug("Market data processing task stopped");
        }

        private bool ValidateTick(Tick tick)
        {
            // Check if tick is valid
            if (!tick.IsValid)
            {
                _logger?.LogWarning("Invalid tick rejected: {Symbol} - Bid: {Bid}, Ask: {Ask}, BidSize: {BidSize}, AskSize: {AskSize}",
                    tick.Symbol, tick.Bid, tick.Ask, tick.BidSize, tick.AskSize);
                return false;
            }

            // Check if tick is stale
            if (tick.IsStale(_staleThresholdMs))
            {
                _logger?.LogWarning("Stale tick rejected: {Symbol} (Age: {Age}ms)",
                    tick.Symbol, tick.Age.TotalMilliseconds);
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

        /// <summary>
        /// Async disposal pattern implementation
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            _disposed = true;
            _logger?.LogInformation("Disposing market data processor");

            try
            {
                // Stop processing
                _processingCts?.Cancel();
                _inputChannel.Writer.TryComplete();

                // Wait for processing task to complete
                if (_processingTask != null)
                {
                    try
                    {
                        await _processingTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                        _logger?.LogWarning("Processing task did not complete within timeout during disposal");
                    }
                }

                // Dispose resources
                _processingCts?.Dispose();

                // Dispose tick history buffers
                foreach (var buffer in _tickHistory.Values)
                {
                    buffer.Dispose();
                }
                _tickHistory.Clear();
                _statistics.Clear();

                _logger?.LogInformation("Market data processor disposed successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during market data processor disposal");
                throw;
            }
        }

        /// <summary>
        /// Synchronous dispose for IDisposable compatibility
        /// </summary>
        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}