using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingEngine.Console.Configuration;
using TradingEngine.Console.Display;
using TradingEngine.Console.Extensions;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.Infrastructure.Pipeline;

namespace TradingEngine.Console
{
    internal class Program
    {
        private static readonly CancellationTokenSource _shutdownCts = new();
        private static TradingPipeline? _pipeline;
        private static TradingConfiguration? _tradingConfig;

        static async Task Main(string[] args)
        {
            try
            {
                var configuration = BuildConfiguration();
                var services = ConfigureServices(configuration);
                var serviceProvider = services.BuildServiceProvider();

                _tradingConfig = serviceProvider.GetRequiredService<IOptions<TradingConfiguration>>().Value;

                await RunTradingEngineAsync(serviceProvider);
            }
            catch (OperationCanceledException)
            {
                StatisticsDisplay.DisplayShutdownSignal();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("configuration"))
            {
                StatisticsDisplay.DisplayFatalError(new Exception("Configuration error: Please check appsettings.json", ex));
            }
            catch (ArgumentException ex)
            {
                StatisticsDisplay.DisplayFatalError(new Exception("Invalid configuration values", ex));
            }
            catch (FileNotFoundException ex) when (ex.Message.Contains("appsettings"))
            {
                StatisticsDisplay.DisplayFatalError(new Exception("appsettings.json not found", ex));
            }
            catch (Exception ex)
            {
                StatisticsDisplay.DisplayFatalError(ex);
            }
        }

        private static IConfiguration BuildConfiguration()
        {
            return new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();
        }

        private static IServiceCollection ConfigureServices(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            return services.AddTradingServices(configuration);
        }

        private static async Task RunTradingEngineAsync(IServiceProvider serviceProvider)
        {
            StatisticsDisplay.DisplayHeader();

            // Setup Ctrl+C handler
            System.Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                _shutdownCts.Cancel();
                StatisticsDisplay.DisplayShutdownSignal();
            };

            // Get the pipeline
            _pipeline = serviceProvider.GetRequiredService<TradingPipeline>();

            // Subscribe to pipeline events
            _pipeline.PipelineEvent += OnPipelineEvent;

            // Configure symbols to trade
            var symbols = _tradingConfig!.Symbols
                .Select(Symbol.Create)
                .ToArray();

            StatisticsDisplay.DisplayStartupMessage(_tradingConfig.Symbols);

            // Start the pipeline
            await _pipeline.StartAsync(symbols);

            // Subscribe to events for monitoring
            var eventBus = serviceProvider.GetRequiredService<IEventBus>();
            SubscribeToEvents(eventBus);

            // Display real-time statistics
            StatisticsDisplay.DisplayRunningMessage();
            await StatisticsDisplay.DisplayLiveStatisticsLoop(_pipeline, _shutdownCts.Token, _tradingConfig.StatisticsUpdateIntervalMs);

            // Shutdown
            StatisticsDisplay.DisplayShutdownMessage();
            await _pipeline.StopAsync();

            // Display final statistics
            StatisticsDisplay.DisplayFinalStatistics(_pipeline);
            StatisticsDisplay.DisplayCompletionMessage();
        }

        private static void SubscribeToEvents(IEventBus eventBus)
        {
            // Subscribe to tick events (synchronous handlers)
            eventBus.Subscribe<TickReceivedEvent>(async e =>
            {
                // System.Console.WriteLine($"[TICK] {e.Tick.Symbol}: Bid={e.Tick.Bid} Ask={e.Tick.Ask}");
                await Task.CompletedTask;
            });

            // Subscribe to signals
            eventBus.Subscribe<SignalGeneratedEvent>(async e =>
            {
                var color = e.Side == Domain.Enums.OrderSide.Buy
                    ? ConsoleColor.Green
                    : ConsoleColor.Red;
                StatisticsDisplay.WriteColoredLine($"[SIGNAL] {e.Symbol} {e.Side} {e.Quantity} {e.SignalType}", color);
                await Task.CompletedTask;
            });

            // Subscribe to orders
            eventBus.Subscribe<OrderPlacedEvent>(async e =>
            {
                System.Console.WriteLine($"[ORDER] {e.Order.Symbol} {e.Order.Side} {e.Order.Quantity} @ {e.Order.Type}");
                await Task.CompletedTask;
            });

            // Subscribe to executions
            eventBus.Subscribe<OrderExecutedEvent>(async e =>
            {
                StatisticsDisplay.WriteColoredLine($"[EXECUTED] {e.Trade.Symbol} {e.Trade.ExecutionQuantity} @ {e.Trade.ExecutionPrice}", ConsoleColor.Cyan);
                await Task.CompletedTask;
            });

            // Subscribe to P&L updates
            eventBus.Subscribe<PnLUpdatedEvent>(async e =>
            {
                if (e.TotalPnL != 0)
                {
                    var color = e.TotalPnL > 0 ? ConsoleColor.Green : ConsoleColor.Red;
                    StatisticsDisplay.WriteColoredLine($"[P&L] Total: {e.TotalPnL:C} (Realized: {e.RealizedPnL:C}, Unrealized: {e.UnrealizedPnL:C})", color);
                }
                await Task.CompletedTask;
            });

            // Subscribe to risk events
            eventBus.Subscribe<RiskLimitBreachedEvent>(async e =>
            {
                StatisticsDisplay.WriteColoredLine($"[RISK BREACH] {e.Description} - Current: {e.CurrentValue:F2}, Limit: {e.LimitValue:F2}", ConsoleColor.Yellow);
                await Task.CompletedTask;
            });

            // Subscribe to system events
            eventBus.Subscribe<SystemEvent>(async e =>
            {
                System.Console.WriteLine($"[SYSTEM] [{e.Level}] {e.Message}");
                await Task.CompletedTask;
            });
        }

        private static void OnPipelineEvent(object? sender, PipelineEventArgs e)
        {
            var color = e.EventType switch
            {
                PipelineEventType.Started => ConsoleColor.Green,
                PipelineEventType.Stopped => ConsoleColor.Yellow,
                PipelineEventType.RiskBreach => ConsoleColor.Red,
                PipelineEventType.Error => ConsoleColor.Red,
                PipelineEventType.Warning => ConsoleColor.Yellow,
                _ => ConsoleColor.White
            };

            StatisticsDisplay.WriteColoredLine($"[PIPELINE] {e.EventType}: {e.Message}", color);
        }
    }
}
