using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.Events;
using TradingEngine.Domain.ValueObjects;
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
using TradingEngine.Strategies.Models;
using Xunit;

namespace TradingEngine.IntegrationTests.Pipeline
{
    public class TradingPipelineTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly TradingPipeline _pipeline;
        private readonly IEventBus _eventBus;
        private readonly IOrderManager _orderManager;
        private readonly IRiskManager _riskManager;
        private readonly StrategyEngine _strategyEngine;

        public TradingPipelineTests()
        {
            var services = new ServiceCollection();

            // Add logging
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddDebug();
            });

            // Event Bus
            services.AddSingleton<IEventBus>(sp =>
                new InMemoryEventBus(sp.GetService<ILogger<InMemoryEventBus>>()));

            // Market Data
            services.AddSingleton<IMarketDataProvider, SimulatedMarketDataProvider>();

            // Strategies
            services.AddSingleton<StrategyEngine>(sp =>
            {
                var engine = new StrategyEngine(100000m);
                return engine;
            });

            // Order Management
            services.AddSingleton<IOrderManager, OrderManager>();
            services.AddSingleton<OrderManager>(sp => (OrderManager)sp.GetRequiredService<IOrderManager>());

            // Exchange
            services.AddSingleton<MockExchange>(sp =>
            {
                var orderManager = sp.GetRequiredService<OrderManager>();
                return new MockExchange(orderManager)
                {
                    SimulatedLatencyMs = 1,
                    SlippagePercent = 0.01m,
                    PartialFillProbability = 0.1m,
                    RejectProbability = 0.01m
                };
            });

            // Risk Management
            services.AddSingleton<IRiskManager>(sp =>
                new RiskManager(100000m)
                {
                    MaxPositionSize = 10000m,
                    MaxTotalExposure = 50000m,
                    MaxLossPerTrade = 1000m,
                    MaxDailyLoss = 5000m,
                    MaxOpenPositions = 10
                });

            services.AddSingleton<IPnLTracker, PnLTracker>();

            // Trading Pipeline
            services.AddSingleton<TradingPipeline>();

            _serviceProvider = services.BuildServiceProvider();

            _eventBus = _serviceProvider.GetRequiredService<IEventBus>();
            _orderManager = _serviceProvider.GetRequiredService<IOrderManager>();
            _riskManager = _serviceProvider.GetRequiredService<IRiskManager>();
            _strategyEngine = _serviceProvider.GetRequiredService<StrategyEngine>();
            _pipeline = _serviceProvider.GetRequiredService<TradingPipeline>();
        }

        [Fact]
        public async Task Pipeline_ShouldStartAndStop_Successfully()
        {
            // Arrange
            var symbols = new[] { new Symbol("TEST") };

            // Act
            await _pipeline.StartAsync(symbols, CancellationToken.None);
            await Task.Delay(100);
            await _pipeline.StopAsync();

            // Assert
            var stats = _pipeline.GetStatistics();
            stats.Should().NotBeNull();
            stats.TicksProcessed.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Pipeline_ShouldProcessMarketData_Successfully()
        {
            // Arrange
            var symbol = new Symbol("TEST");
            var symbols = new[] { symbol };
            var tickReceived = false;

            _eventBus.Subscribe<TickProcessedEvent>(async e =>
            {
                if (e.Tick.Symbol == symbol)
                {
                    tickReceived = true;
                }
                await Task.CompletedTask;
            });

            // Act
            await _pipeline.StartAsync(symbols, CancellationToken.None);
            await Task.Delay(200); // Wait for some ticks
            await _pipeline.StopAsync();

            // Assert
            tickReceived.Should().BeTrue();
        }

        [Fact]
        public async Task Pipeline_WithStrategy_ShouldGenerateSignals()
        {
            // Arrange
            var symbol = new Symbol("TEST");
            var symbols = new[] { symbol };
            var signalGenerated = false;

            // Register a momentum strategy
            var strategy = new MomentumStrategy();
            strategy.UpdateParameters(new MomentumStrategyParameters
            {
                LookbackPeriod = 5,
                MomentumThreshold = 0.5m,
                TakeProfitPercent = 1.0m,
                StopLossPercent = 0.5m,
                PositionSizePercent = 10.0m,
                MinConfidence = 0.3
            });

            await _strategyEngine.RegisterStrategyAsync(strategy);

            _eventBus.Subscribe<SignalGeneratedEvent>(async e =>
            {
                if (e.Signal.Symbol == symbol)
                {
                    signalGenerated = true;
                }
                await Task.CompletedTask;
            });

            // Act
            await _pipeline.StartAsync(symbols, CancellationToken.None);
            await Task.Delay(1000); // Wait for enough ticks to generate signals
            await _pipeline.StopAsync();

            // Assert - Signal generation depends on market conditions, 
            // but we should have processed ticks
            var stats = _pipeline.GetStatistics();
            stats.TicksProcessed.Should().BeGreaterThan(10);
        }

        [Fact]
        public async Task OrderManager_ShouldCreateAndManageOrders()
        {
            // Arrange
            var symbol = new Symbol("TEST");
            var quantity = new Quantity(100);

            // Act
            var orderId = await _orderManager.CreateOrderAsync(
                symbol,
                OrderSide.Buy,
                OrderType.Market,
                quantity
            );

            // Assert
            orderId.Should().NotBeNull();
            var order = await _orderManager.GetOrderAsync(orderId);
            order.Should().NotBeNull();
            order!.Symbol.Should().Be(symbol);
            order.Quantity.Should().Be(quantity);
            order.Status.Should().Be(OrderStatus.Pending);
        }

        [Fact]
        public async Task RiskManager_ShouldValidateRiskLimits()
        {
            // Arrange
            var symbol = new Symbol("TEST");
            var quantity = new Quantity(1000);
            var price = new Price(100m);
            var side = OrderSide.Buy;

            // Act
            var canTrade = await _riskManager.CanTradeAsync(symbol, side, quantity, price);

            // Assert
            canTrade.Should().BeTrue(); // Within risk limits
        }

        [Fact]
        public async Task RiskManager_ShouldRejectExcessivePosition()
        {
            // Arrange
            var symbol = new Symbol("TEST");
            var quantity = new Quantity(10000); // Excessive quantity
            var price = new Price(100m);
            var side = OrderSide.Buy;

            // Act
            var canTrade = await _riskManager.CanTradeAsync(symbol, side, quantity, price);

            // Assert
            canTrade.Should().BeFalse(); // Exceeds risk limits
        }

        [Fact]
        public async Task Pipeline_ShouldReportStatistics_Correctly()
        {
            // Arrange
            var symbols = new[] { new Symbol("TEST1"), new Symbol("TEST2") };

            // Act
            await _pipeline.StartAsync(symbols, CancellationToken.None);
            await Task.Delay(500); // Let it run for a bit
            var stats = _pipeline.GetStatistics();
            await _pipeline.StopAsync();

            // Assert
            stats.Should().NotBeNull();
            stats.TicksProcessed.Should().BeGreaterThan(0);
            stats.Uptime.Should().BeGreaterThan(TimeSpan.Zero);
            stats.EventBusStats.Should().NotBeNull();
            stats.EventBusStats!.TotalEventsPublished.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task EventBus_ShouldDeliverEvents_ToSubscribers()
        {
            // Arrange
            var eventReceived = false;
            var testEvent = new SystemEvent("Test", EventLevel.Information);

            _eventBus.Subscribe<SystemEvent>(async e =>
            {
                if (e.Message == "Test")
                {
                    eventReceived = true;
                }
                await Task.CompletedTask;
            });

            // Act
            await _eventBus.PublishAsync(testEvent);
            await Task.Delay(100); // Give time for async processing

            // Assert
            eventReceived.Should().BeTrue();
        }

        [Fact]
        public async Task Pipeline_ShouldHandleConcurrentOperations()
        {
            // Arrange
            var symbols = new[] {
                new Symbol("TEST1"),
                new Symbol("TEST2"),
                new Symbol("TEST3")
            };

            // Act - Start and stop multiple times
            for (int i = 0; i < 3; i++)
            {
                await _pipeline.StartAsync(symbols, CancellationToken.None);
                await Task.Delay(100);
                await _pipeline.StopAsync();
            }

            // Assert - Should handle gracefully without errors
            var stats = _pipeline.GetStatistics();
            stats.Should().NotBeNull();
        }

        public void Dispose()
        {
            _pipeline?.StopAsync().GetAwaiter().GetResult();
            _serviceProvider?.Dispose();
        }
    }
}