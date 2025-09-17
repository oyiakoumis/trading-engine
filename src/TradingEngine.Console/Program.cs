using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Exchange;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Execution.Services;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.Infrastructure.Pipeline;
using TradingEngine.MarketData.Interfaces;
using TradingEngine.MarketData.Providers;
using TradingEngine.Risk.Interfaces;
using TradingEngine.Risk.Services;
using TradingEngine.Strategies.Engine;
using TradingEngine.Strategies.Implementations;
using TradingEngine.Strategies.Interfaces;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Console
{
    class Program
    {
        private static CancellationTokenSource _shutdownCts = new();
        private static TradingPipeline? _pipeline;

        static async Task Main(string[] args)
        {
            System.Console.WriteLine("===========================================");
            System.Console.WriteLine("     TICK-TO-TRADE PIPELINE DEMO");
            System.Console.WriteLine("===========================================");
            System.Console.WriteLine();

            // Setup Ctrl+C handler
            System.Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                _shutdownCts.Cancel();
                System.Console.WriteLine("\nShutdown signal received...");
            };

            try
            {
                // Configure services
                var services = ConfigureServices();
                var serviceProvider = services.BuildServiceProvider();

                // Get the pipeline
                _pipeline = serviceProvider.GetRequiredService<TradingPipeline>();

                // Subscribe to pipeline events
                _pipeline.PipelineEvent += OnPipelineEvent;

                // Configure symbols to trade
                var symbols = new[]
                {
                    Symbol.Create("AAPL"),
                    Symbol.Create("MSFT"),
                    Symbol.Create("GOOGL")
                };

                System.Console.WriteLine("Starting Trading Pipeline...");
                System.Console.WriteLine($"Trading symbols: {string.Join(", ", symbols.Select(s => s.Value))}");
                System.Console.WriteLine();

                // Start the pipeline
                await _pipeline.StartAsync(symbols);

                // Subscribe to events for monitoring
                var eventBus = serviceProvider.GetRequiredService<IEventBus>();
                SubscribeToEvents(eventBus);

                // Display real-time statistics
                await DisplayStatisticsLoop(_shutdownCts.Token);

                // Shutdown
                System.Console.WriteLine("\nShutting down...");
                await _pipeline.StopAsync();

                // Display final statistics
                DisplayFinalStatistics();

                System.Console.WriteLine("\nTrade Engine demonstration completed.");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Fatal error: {ex.Message}");
                System.Console.WriteLine(ex.StackTrace);
            }
        }

        private static ServiceCollection ConfigureServices()
        {
            var services = new ServiceCollection();

            // Logging
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "HH:mm:ss ";
                });
            });

            // Event Bus
            services.AddSingleton<IEventBus>(sp =>
                new InMemoryEventBus(sp.GetService<ILogger<InMemoryEventBus>>()));

            // Market Data
            services.AddSingleton<IMarketDataProvider, SimulatedMarketDataProvider>();

            // Strategies
            services.AddSingleton<StrategyEngine>(sp =>
            {
                var engine = new StrategyEngine(100000m); // $100k initial capital

                // Register momentum strategy
                var momentumStrategy = new MomentumStrategy();
                momentumStrategy.UpdateParameters(new MomentumStrategyParameters
                {
                    LookbackPeriod = 20,
                    MomentumThreshold = 2.0m,
                    TakeProfitPercent = 2.0m,
                    StopLossPercent = 1.0m,
                    PositionSizePercent = 10.0m,
                    MinConfidence = 0.6
                });

                // Register the strategy with the engine (async in sync context)
                engine.RegisterStrategyAsync(momentumStrategy).GetAwaiter().GetResult();

                return engine;
            });

            services.AddTransient<IStrategy, MomentumStrategy>();

            // Order Management
            services.AddSingleton<IOrderManager, OrderManager>();
            services.AddSingleton<OrderManager>(sp => (OrderManager)sp.GetRequiredService<IOrderManager>());

            // Exchange
            services.AddSingleton<MockExchange>(sp =>
            {
                var orderManager = sp.GetRequiredService<OrderManager>();
                return new MockExchange(orderManager)
                {
                    SimulatedLatencyMs = 10,
                    SlippagePercent = 0.01m,
                    PartialFillProbability = 0.2m,
                    RejectProbability = 0.02m
                };
            });

            // Risk Management
            services.AddSingleton<IRiskManager>(sp =>
                new RiskManager(100000m)
                {
                    // Configure risk limits
                });

            // P&L Tracking
            services.AddSingleton<PnLTracker>(sp =>
                new PnLTracker(100000m));

            // Trading Pipeline
            services.AddSingleton<TradingPipeline>(sp =>
            {
                var logger = sp.GetService<ILogger<TradingPipeline>>();
                return new TradingPipeline(
                    sp.GetRequiredService<IMarketDataProvider>(),
                    sp.GetRequiredService<StrategyEngine>(),
                    sp.GetRequiredService<IOrderManager>(),
                    sp.GetRequiredService<MockExchange>(),
                    sp.GetRequiredService<IRiskManager>(),
                    sp.GetRequiredService<PnLTracker>(),
                    sp.GetRequiredService<IEventBus>(),
                    logger
                )
                {
                    InitialCapital = 100000m,
                    TickHistorySize = 100
                };
            });

            return services;
        }

        private static void SubscribeToEvents(IEventBus eventBus)
        {
            // Subscribe to tick events
            eventBus.Subscribe<TickReceivedEvent>(async e =>
            {
                // Log high-level tick info (commented out to reduce noise)
                // System.Console.WriteLine($"[TICK] {e.Tick.Symbol}: Bid={e.Tick.Bid} Ask={e.Tick.Ask}");
                await Task.CompletedTask;
            });

            // Subscribe to signals
            eventBus.Subscribe<SignalGeneratedEvent>(async e =>
            {
                var color = e.Side == Domain.Enums.OrderSide.Buy
                    ? ConsoleColor.Green
                    : ConsoleColor.Red;

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
                WriteColoredLine($"[EXECUTED] {e.Trade.Symbol} {e.Trade.ExecutionQuantity} @ {e.Trade.ExecutionPrice}", ConsoleColor.Cyan);
                await Task.CompletedTask;
            });

            // Subscribe to P&L updates
            eventBus.Subscribe<PnLUpdatedEvent>(async e =>
            {
                if (e.TotalPnL != 0)
                {
                    var color = e.TotalPnL > 0 ? ConsoleColor.Green : ConsoleColor.Red;
                    WriteColoredLine($"[P&L] Total: {e.TotalPnL:C} (Realized: {e.RealizedPnL:C}, Unrealized: {e.UnrealizedPnL:C})", color);
                }
                await Task.CompletedTask;
            });

            // Subscribe to risk events
            eventBus.Subscribe<RiskLimitBreachedEvent>(async e =>
            {
                WriteColoredLine($"[RISK BREACH] {e.Description} - Current: {e.CurrentValue:F2}, Limit: {e.LimitValue:F2}", ConsoleColor.Yellow);
                await Task.CompletedTask;
            });

            // Subscribe to system events
            eventBus.Subscribe<SystemEvent>(async e =>
            {
                System.Console.WriteLine($"[SYSTEM] [{e.Level}] {e.Message}");
                await Task.CompletedTask;
            });
        }

        private static async Task DisplayStatisticsLoop(CancellationToken cancellationToken)
        {
            var lastDisplay = DateTime.UtcNow;

            System.Console.WriteLine("\nTrading system is running. Press Ctrl+C to stop.\n");
            System.Console.WriteLine("Live Statistics:");
            System.Console.WriteLine("================");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, cancellationToken); // Update every 5 seconds

                    if (_pipeline != null)
                    {
                        var stats = _pipeline.GetStatistics();

                        // Clear previous stats lines and rewrite
                        int currentTop = System.Console.CursorTop;
                        int targetTop = Math.Max(0, currentTop - 5);

                        if (targetTop >= 0 && targetTop < System.Console.BufferHeight)
                        {
                            System.Console.SetCursorPosition(0, targetTop);
                            System.Console.WriteLine($"Uptime:     {stats.Uptime:hh\\:mm\\:ss}                    ");
                            System.Console.WriteLine($"Ticks:      {stats.TicksProcessed:N0}                  ");
                            System.Console.WriteLine($"Signals:    {stats.SignalsGenerated:N0}                ");
                            System.Console.WriteLine($"Orders:     {stats.OrdersExecuted:N0}                  ");
                            System.Console.WriteLine($"Positions:  {stats.ActivePositions}                    ");
                            System.Console.WriteLine();
                        }
                        else
                        {
                            // If we can't reposition, just write normally
                            System.Console.WriteLine($"\rUptime: {stats.Uptime:hh\\:mm\\:ss} | Ticks: {stats.TicksProcessed:N0} | Signals: {stats.SignalsGenerated:N0} | Orders: {stats.OrdersExecuted:N0} | Positions: {stats.ActivePositions}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static void DisplayFinalStatistics()
        {
            if (_pipeline == null) return;

            var stats = _pipeline.GetStatistics();

            System.Console.WriteLine("\n===========================================");
            System.Console.WriteLine("          FINAL STATISTICS");
            System.Console.WriteLine("===========================================");
            System.Console.WriteLine($"Total Runtime:        {stats.Uptime:hh\\:mm\\:ss}");
            System.Console.WriteLine($"Ticks Processed:      {stats.TicksProcessed:N0}");
            System.Console.WriteLine($"Signals Generated:    {stats.SignalsGenerated:N0}");
            System.Console.WriteLine($"Orders Executed:      {stats.OrdersExecuted:N0}");
            System.Console.WriteLine($"Active Positions:     {stats.ActivePositions}");

            if (stats.EventBusStats != null)
            {
                System.Console.WriteLine($"Events Published:     {stats.EventBusStats.TotalEventsPublished:N0}");
                System.Console.WriteLine($"Events Processed:     {stats.EventBusStats.TotalEventsProcessed:N0}");
                System.Console.WriteLine($"Avg Processing Time:  {stats.EventBusStats.AverageProcessingTime.TotalMilliseconds:F2}ms");
            }

            System.Console.WriteLine("===========================================");
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
        }

        private static void WriteColoredLine(string message, ConsoleColor color)
        {
            var originalColor = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            System.Console.WriteLine(message);
            System.Console.ForegroundColor = originalColor;
        }
    }
}
