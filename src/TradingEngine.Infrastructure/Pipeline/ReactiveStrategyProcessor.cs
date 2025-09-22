using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.Strategies.Engine;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Reactive strategy processor using channels instead of polling
    /// </summary>
    public class ReactiveStrategyProcessor : IDisposable
    {
        private readonly StrategyEngine _strategyEngine;
        private readonly IEventBus _eventBus;
        private readonly ILogger<ReactiveStrategyProcessor>? _logger;
        private readonly Channel<Signal> _signalChannel;
        private readonly CancellationTokenSource _processingCts;
        private readonly Task _processingTask;
        private readonly PipelineStatisticsCollector _statisticsCollector;
        private bool _disposed;

        public ReactiveStrategyProcessor(
            StrategyEngine strategyEngine,
            IEventBus eventBus,
            PipelineStatisticsCollector statisticsCollector,
            ILogger<ReactiveStrategyProcessor>? logger = null)
        {
            _strategyEngine = strategyEngine;
            _eventBus = eventBus;
            _statisticsCollector = statisticsCollector;
            _logger = logger;
            
            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            };
            
            _signalChannel = Channel.CreateUnbounded<Signal>(channelOptions);
            _processingCts = new CancellationTokenSource();
            
            // Subscribe to strategy engine signals
            _strategyEngine.SignalGenerated += OnSignalGenerated;
            
            // Start processing task
            _processingTask = ProcessSignalsAsync(_processingCts.Token);
        }

        private void OnSignalGenerated(object? sender, Signal signal)
        {
            if (!_signalChannel.Writer.TryWrite(signal))
            {
                _logger?.LogWarning("Failed to enqueue signal for {Symbol}", signal.Symbol);
            }
        }

        private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
        {
            _logger?.LogInformation("Strategy signal processing started");

            try
            {
                await foreach (var signal in _signalChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Publish signal event
                        await _eventBus.PublishAsync(new SignalGeneratedEvent(
                            signal.Symbol,
                            signal.Side,
                            signal.Quantity,
                            signal.Type.ToString(),
                            signal.Confidence,
                            signal.Reason
                        ), cancellationToken);

                        _statisticsCollector.IncrementSignalsGenerated();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing signal for {Symbol}", signal.Symbol);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Strategy signal processing failed");
            }
            finally
            {
                _logger?.LogInformation("Strategy signal processing stopped");
            }
        }

        public void Stop()
        {
            _processingCts.Cancel();
        }

        public async Task StopAsync()
        {
            Stop();
            try
            {
                await _processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _strategyEngine.SignalGenerated -= OnSignalGenerated;
            
            _processingCts.Cancel();
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during reactive strategy processor shutdown");
            }

            _processingCts.Dispose();
            _signalChannel.Writer.TryComplete();
        }
    }
}