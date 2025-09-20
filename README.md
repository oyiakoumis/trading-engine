# C# Trading Engine (Demo)

A C# tick-to-trade pipeline implementing real-time market data handling, pluggable strategy execution, order lifecycle management, and risk control. Built with concurrent processing, lock-free data structures, and domain-driven design for low-latency trading simulation.

## 🏗️ Architecture Overview

This trading engine implements a complete tick-to-trade pipeline with the following components:

### Core Components

- **Market Data Feed**: Real-time tick generation and processing with validation
- **Strategy Engine**: Pluggable strategy framework with momentum-based signal generation
- **Order Management System (OMS)**: Complete order lifecycle management with state transitions
- **Execution Management System (EMS)**: Mock exchange with realistic fill simulation
- **Risk Management**: Pre-trade and post-trade risk checks with position limits
- **P&L Tracking**: Real-time P&L calculation with performance metrics
- **Event Bus**: Asynchronous event-driven architecture for component communication

### Technical Highlights

- **Multi-threaded Processing**: Dedicated threads for market data, strategies, orders, and risk
- **Lock-free Data Structures**: High-performance concurrent collections
- **Async/Await Pattern**: Non-blocking I/O operations throughout
- **Domain-Driven Design**: Rich domain models with value objects
- **SOLID Principles**: Clean architecture with dependency injection
- **Design Patterns**: Strategy, Observer, CQRS, Repository patterns

## 🚀 Quick Start

```bash
dotnet build src/TradingEngine.Console/TradingEngine.Console.csproj && dotnet run --project src/TradingEngine.Console
```

## System Features

### Market Data Processing
- Simulated tick generation for multiple symbols
- Random walk price simulation with realistic volatility
- Tick validation and filtering
- Support for bid/ask spreads

### Trading Strategies
- **Momentum Strategy**: Detects price momentum with configurable parameters
  - Lookback period analysis
  - Momentum threshold detection
  - Dynamic position sizing
  - Take profit/stop loss management

### Order Management
- Complete order lifecycle (New → Pending → Filled/Rejected)
- Support for market and limit orders
- Partial fill handling
- Order routing to appropriate exchanges

### Risk Management
- **Pre-trade checks**:
  - Position limits
  - Order size validation
  - Available capital verification
  - Maximum exposure limits
  
- **Post-trade monitoring**:
  - Real-time P&L tracking
  - Drawdown monitoring
  - Risk metric calculation
  - Breach notifications

### Performance Metrics
- Real-time statistics display
- Trade execution latency monitoring
- Throughput measurements (ticks/second)
- P&L tracking with Sharpe ratio calculation

## 📁 Project Structure

```
trading_engine/
├── src/
│   ├── TradingEngine.Domain/          # Core domain models and value objects
│   ├── TradingEngine.MarketData/      # Market data feed and processing
│   ├── TradingEngine.Strategies/      # Trading strategies and signals
│   ├── TradingEngine.Execution/       # Order management and execution
│   ├── TradingEngine.Risk/            # Risk management and P&L tracking
│   ├── TradingEngine.Infrastructure/  # Event bus and pipeline orchestration
│   └── TradingEngine.Console/         # Console application demo
├── tests/
│   ├── TradingEngine.UnitTests/       # Unit tests
└── └── TradingEngine.IntegrationTests/# Integration tests
```

## 🔧 Configuration

The trading engine can be configured through the console application's dependency injection setup:

```csharp
// Strategy parameters
var strategy = new MomentumStrategy();
strategy.UpdateParameters(new MomentumStrategyParameters
{
    LookbackPeriod = 20,        // Number of ticks to analyze
    MomentumThreshold = 2.0m,    // Standard deviations for signal
    TakeProfitPercent = 2.0m,    // Take profit at 2% gain
    StopLossPercent = 1.0m,      // Stop loss at 1% loss
    PositionSizePercent = 10.0m, // Use 10% of capital per position
    MinConfidence = 0.6          // Minimum confidence for trades
});

// Risk limits
var riskManager = new RiskManager(100000m)  // $100k initial capital
{
    MaxPositionSize = 10000m,
    MaxTotalExposure = 50000m,
    MaxLossPerTrade = 1000m,
    MaxDailyLoss = 5000m,
    MaxOpenPositions = 10
};
```
