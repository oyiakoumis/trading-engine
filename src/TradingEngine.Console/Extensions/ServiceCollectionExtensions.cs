using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Console.Configuration;
using TradingEngine.Execution.Exchange;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Execution.Services;
using TradingEngine.Infrastructure.EventBus;
using TradingEngine.Infrastructure.Pipeline;
using TradingEngine.MarketData.Interfaces;
using TradingEngine.MarketData.Processors;
using TradingEngine.MarketData.Providers;
using TradingEngine.Risk.Interfaces;
using TradingEngine.Risk.Services;
using TradingEngine.Strategies.Engine;
using TradingEngine.Strategies.Implementations;
using TradingEngine.Strategies.Interfaces;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Console.Extensions
{
    internal static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddTradingServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Configure options
            services.Configure<TradingConfiguration>(configuration.GetSection("Trading"));

            // Add domain services
            services.AddLoggingServices(configuration);
            services.AddEventBusServices();
            services.AddMarketDataServices();
            services.AddStrategyServices(configuration);
            services.AddExecutionServices();
            services.AddRiskServices(configuration);
            services.AddTradingPipeline(configuration);

            return services;
        }

        private static IServiceCollection AddLoggingServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddLogging(builder =>
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddSimpleConsole(options =>
                {
                    options.TimestampFormat = configuration["Logging:Console:TimestampFormat"] ?? "HH:mm:ss ";
                });
            });

            return services;
        }

        private static IServiceCollection AddEventBusServices(this IServiceCollection services)
        {
            services.AddSingleton<IEventBus, InMemoryEventBus>();
            return services;
        }

        private static IServiceCollection AddMarketDataServices(this IServiceCollection services)
        {
            services.AddSingleton<IMarketDataProvider, SimulatedMarketDataProvider>();

            // Add MarketDataProcessor with proper interface registration
            services.AddSingleton<MarketDataProcessor>(sp =>
            {
                var logger = sp.GetService<ILogger<MarketDataProcessor>>();
                return new MarketDataProcessor(logger);
            });

            services.AddSingleton<IMarketDataProcessor>(sp => sp.GetRequiredService<MarketDataProcessor>());

            return services;
        }

        private static IServiceCollection AddStrategyServices(this IServiceCollection services, IConfiguration configuration)
        {
            var tradingConfig = configuration.GetSection("Trading").Get<TradingConfiguration>() ?? new TradingConfiguration();

            services.AddSingleton<StrategyEngine>(sp =>
            {
                var engine = new StrategyEngine(tradingConfig.InitialCapital);

                // Register momentum strategy
                var momentumStrategy = new MomentumStrategy();
                momentumStrategy.UpdateParameters(new MomentumStrategyParameters
                {
                    LookbackPeriod = tradingConfig.Strategy.Momentum.LookbackPeriod,
                    MomentumThreshold = tradingConfig.Strategy.Momentum.MomentumThreshold,
                    TakeProfitPercent = tradingConfig.Strategy.Momentum.TakeProfitPercent,
                    StopLossPercent = tradingConfig.Strategy.Momentum.StopLossPercent,
                    PositionSizePercent = tradingConfig.Strategy.Momentum.PositionSizePercent,
                    MinConfidence = tradingConfig.Strategy.Momentum.MinConfidence
                });

                // Use Task.Run to avoid deadlock risk
                Task.Run(async () => await engine.RegisterStrategyAsync(momentumStrategy)).Wait();

                return engine;
            });

            services.AddTransient<IStrategy, MomentumStrategy>();
            return services;
        }

        private static IServiceCollection AddExecutionServices(this IServiceCollection services)
        {
            // Register core order processor
            services.AddSingleton<TradingEngine.Execution.Services.OrderProcessor>();
            
            // Register adapters for existing services
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.IRiskAssessment>(sp =>
                new TradingEngine.Execution.Adapters.RiskManagerAdapter(sp.GetRequiredService<IRiskManager>()));
            
            // Register supporting services
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.ISignalToOrderConverter,
                TradingEngine.Execution.Pipeline.Stages.SignalToOrderConverter>();
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.IRateLimiter>(sp =>
                new TradingEngine.Execution.Adapters.SimpleRateLimiter(100)); // 100 orders per minute
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.IBulkheadIsolation>(sp =>
                new TradingEngine.Execution.Adapters.SimpleBulkheadIsolation(10)); // 10 concurrent operations
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.IMetricsCollector,
                TradingEngine.Execution.Adapters.InMemoryMetricsCollector>();
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.IEventBus>(sp =>
                new TradingEngine.Execution.Adapters.SimpleEventBusAdapter(sp.GetRequiredService<TradingEngine.Infrastructure.EventBus.IEventBus>()));
            
            // Register the new order processing pipeline system
            services.AddSingleton<TradingEngine.Execution.Pipeline.Interfaces.IOrderProcessingPipeline,
                TradingEngine.Execution.Pipeline.OrderProcessingPipeline>();
            
            // Register processing stages
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.ValidationStage>();
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.RiskAssessmentStage>();
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.CreationStage>();
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.ExecutionStage>();
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.PostProcessingStage>();
            
            // Register resilience components
            services.AddSingleton<TradingEngine.Execution.Resilience.ICircuitBreaker>(sp =>
                TradingEngine.Execution.Resilience.CircuitBreakerFactory.CreateForExchange(
                    sp.GetService<ILogger<TradingEngine.Execution.Resilience.CircuitBreaker>>()));
            
            // Register command handlers and supporting services
            services.AddSingleton<TradingEngine.Execution.Commands.IOrderCommandHandler>(sp =>
                sp.GetRequiredService<TradingEngine.Execution.Services.OrderProcessor>());
            services.AddSingleton<TradingEngine.Execution.Pipeline.Stages.IOrderTracker>(sp =>
                sp.GetRequiredService<TradingEngine.Execution.Services.OrderProcessor>());
            
            // Register the order processing coordinator
            services.AddSingleton<TradingEngine.Execution.Pipeline.OrderProcessingCoordinator>();
            
            // Keep the old services for backward compatibility
            services.AddSingleton<IOrderManager, OrderManager>();
            services.AddSingleton<OrderManager>(sp => (OrderManager)sp.GetRequiredService<IOrderManager>());

            // Add OrderRouter (kept for compatibility)
            services.AddSingleton<IOrderRouter, OrderRouter>(sp =>
            {
                var orderManager = sp.GetRequiredService<IOrderManager>();
                var exchange = sp.GetRequiredService<IExchange>();
                return new OrderRouter(orderManager, exchange);
            });
            services.AddSingleton<OrderRouter>(sp => (OrderRouter)sp.GetRequiredService<IOrderRouter>());

            return services;
        }

        private static IServiceCollection AddRiskServices(this IServiceCollection services, IConfiguration configuration)
        {
            var tradingConfig = configuration.GetSection("Trading").Get<TradingConfiguration>() ?? new TradingConfiguration();

            services.AddSingleton<IRiskManager>(sp =>
                new RiskManager(tradingConfig.InitialCapital));

            services.AddSingleton<PnLTracker>(sp =>
                new PnLTracker(tradingConfig.InitialCapital));

            return services;
        }

        private static IServiceCollection AddTradingPipeline(this IServiceCollection services, IConfiguration configuration)
        {
            var tradingConfig = configuration.GetSection("Trading").Get<TradingConfiguration>() ?? new TradingConfiguration();

            // Add MockExchange
            services.AddSingleton<MockExchange>(sp =>
            {
                var orderManager = sp.GetRequiredService<OrderManager>();
                return new MockExchange(orderManager)
                {
                    SimulatedLatencyMs = tradingConfig.MockExchange.SimulatedLatencyMs,
                    SlippagePercent = tradingConfig.MockExchange.SlippagePercent,
                    PartialFillProbability = (decimal)tradingConfig.MockExchange.PartialFillProbability,
                    RejectProbability = (decimal)tradingConfig.MockExchange.RejectProbability
                };
            });

            // Register IExchange interface
            services.AddSingleton<IExchange>(sp => sp.GetRequiredService<MockExchange>());

            services.AddSingleton<TradingPipeline>(sp =>
            {
                var logger = sp.GetService<ILogger<TradingPipeline>>();
                return new TradingPipeline(
                    sp.GetRequiredService<IMarketDataProvider>(),
                    sp.GetRequiredService<MarketDataProcessor>(),
                    sp.GetRequiredService<StrategyEngine>(),
                    sp.GetRequiredService<TradingEngine.Execution.Pipeline.OrderProcessingCoordinator>(),
                    sp.GetRequiredService<IExchange>(),
                    sp.GetRequiredService<IRiskManager>(),
                    sp.GetRequiredService<PnLTracker>(),
                    sp.GetRequiredService<IEventBus>(),
                    logger
                )
                {
                    InitialCapital = tradingConfig.InitialCapital,
                    TickHistorySize = tradingConfig.TickHistorySize
                };
            });

            return services;
        }
    }
}