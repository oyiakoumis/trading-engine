using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Strategies.Interfaces;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Strategies.Implementations
{
    /// <summary>
    /// Simple momentum-based trading strategy
    /// Generates signals based on price momentum over a lookback period
    /// </summary>
    public class MomentumStrategy : IStrategy
    {
        private MomentumStrategyParameters _parameters;
        private readonly Queue<decimal> _momentumHistory;
        private decimal _lastSignalPrice;
        private SignalType _lastSignalType;

        public string Name => "Simple Momentum Strategy";
        public bool IsEnabled { get; set; }

        public MomentumStrategy()
        {
            _parameters = new MomentumStrategyParameters();
            _momentumHistory = new Queue<decimal>();
            IsEnabled = true;
            _lastSignalType = SignalType.Hold;
        }

        public async Task InitializeAsync()
        {
            Reset();
            await Task.CompletedTask;
        }

        public void Reset()
        {
            _momentumHistory.Clear();
            _lastSignalPrice = 0;
            _lastSignalType = SignalType.Hold;
        }

        public void UpdateParameters(StrategyParameters parameters)
        {
            if (parameters is MomentumStrategyParameters momentumParams)
            {
                _parameters = momentumParams;
            }
            else
            {
                _parameters = new MomentumStrategyParameters();
            }
        }

        public async Task<Signal?> EvaluateAsync(MarketSnapshot snapshot, PositionContext positionContext)
        {
            if (!IsEnabled)
                return null;

            // Calculate momentum
            var momentum = snapshot.CalculateMomentum(_parameters.LookbackPeriod);
            if (!momentum.HasValue)
                return null;

            // Track momentum history
            _momentumHistory.Enqueue(momentum.Value);
            if (_momentumHistory.Count > 10)
                _momentumHistory.Dequeue();

            // Check if we should skip due to risk limits
            if (positionContext.RiskMetrics.CurrentDrawdown > positionContext.RiskMetrics.MaxDailyLossLimit)
            {
                // In max drawdown - only allow exit signals
                if (positionContext.CurrentPosition?.IsOpen == true)
                {
                    return GenerateExitSignal(snapshot, positionContext, "Risk limit reached");
                }
                return null;
            }

            var currentPrice = snapshot.CurrentTick.MidPrice;
            var hasPosition = positionContext.CurrentPosition?.IsOpen == true;

            // Generate signal based on momentum
            if (hasPosition)
            {
                return await EvaluateExitConditionsAsync(snapshot, positionContext, momentum.Value);
            }
            else
            {
                return await EvaluateEntryConditionsAsync(snapshot, positionContext, momentum.Value);
            }
        }

        private async Task<Signal?> EvaluateEntryConditionsAsync(
            MarketSnapshot snapshot,
            PositionContext positionContext,
            decimal momentum)
        {
            var currentPrice = snapshot.CurrentTick.MidPrice;

            // Strong positive momentum - potential long entry
            if (momentum > _parameters.MomentumThreshold)
            {
                var confidence = CalculateConfidence(momentum, true);
                if (confidence < _parameters.MinConfidence)
                    return null;

                // Check if we recently signaled (avoid over-trading)
                if (_lastSignalType == SignalType.Entry &&
                    Math.Abs((currentPrice.Value - _lastSignalPrice) / _lastSignalPrice) < 0.01m)
                {
                    return null;
                }

                var quantity = CalculatePositionSize(positionContext, currentPrice);
                if (quantity.IsZero)
                    return null;

                var stopLoss = CalculateStopLoss(currentPrice, OrderSide.Buy);
                var takeProfit = CalculateTakeProfit(currentPrice, OrderSide.Buy);

                _lastSignalPrice = currentPrice.Value;
                _lastSignalType = SignalType.Entry;

                return new Signal(
                    snapshot.CurrentTick.Symbol,
                    OrderSide.Buy,
                    quantity,
                    SignalType.Entry,
                    confidence,
                    $"Positive momentum: {momentum:F2}%",
                    currentPrice,
                    stopLoss,
                    takeProfit
                );
            }
            // Strong negative momentum - potential short entry
            else if (momentum < -_parameters.MomentumThreshold)
            {
                var confidence = CalculateConfidence(Math.Abs(momentum), false);
                if (confidence < _parameters.MinConfidence)
                    return null;

                if (_lastSignalType == SignalType.Entry &&
                    Math.Abs((currentPrice.Value - _lastSignalPrice) / _lastSignalPrice) < 0.01m)
                {
                    return null;
                }

                var quantity = CalculatePositionSize(positionContext, currentPrice);
                if (quantity.IsZero)
                    return null;

                var stopLoss = CalculateStopLoss(currentPrice, OrderSide.Sell);
                var takeProfit = CalculateTakeProfit(currentPrice, OrderSide.Sell);

                _lastSignalPrice = currentPrice.Value;
                _lastSignalType = SignalType.Entry;

                return new Signal(
                    snapshot.CurrentTick.Symbol,
                    OrderSide.Sell,
                    quantity,
                    SignalType.Entry,
                    confidence,
                    $"Negative momentum: {momentum:F2}%",
                    currentPrice,
                    stopLoss,
                    takeProfit
                );
            }

            await Task.CompletedTask;
            return null;
        }

        private async Task<Signal?> EvaluateExitConditionsAsync(
            MarketSnapshot snapshot,
            PositionContext positionContext,
            decimal momentum)
        {
            var position = positionContext.CurrentPosition!;
            var currentPrice = snapshot.CurrentTick.MidPrice;
            var isLong = position.NetQuantity.Value > 0;
            var entryPrice = position.AverageEntryPrice;
            var pnlPercent = ((currentPrice.Value - entryPrice.Value) / entryPrice.Value) * 100;

            // Check take profit
            if ((isLong && pnlPercent >= _parameters.TakeProfitPercent) ||
                (!isLong && pnlPercent <= -_parameters.TakeProfitPercent))
            {
                return GenerateExitSignal(snapshot, positionContext, $"Take profit reached: {pnlPercent:F2}%");
            }

            // Check stop loss
            if ((isLong && pnlPercent <= -_parameters.StopLossPercent) ||
                (!isLong && pnlPercent >= _parameters.StopLossPercent))
            {
                return GenerateExitSignal(snapshot, positionContext, $"Stop loss triggered: {pnlPercent:F2}%");
            }

            // Check momentum reversal
            if ((isLong && momentum < -_parameters.MomentumThreshold / 2) ||
                (!isLong && momentum > _parameters.MomentumThreshold / 2))
            {
                return GenerateExitSignal(snapshot, positionContext, $"Momentum reversal: {momentum:F2}%");
            }

            // Check if momentum is weakening (partial exit)
            if (_momentumHistory.Count >= 3)
            {
                var recentMomentums = _momentumHistory.TakeLast(3).ToList();
                var isWeakening = isLong
                    ? recentMomentums[0] > recentMomentums[1] && recentMomentums[1] > recentMomentums[2]
                    : recentMomentums[0] < recentMomentums[1] && recentMomentums[1] < recentMomentums[2];

                if (isWeakening && Math.Abs(position.NetQuantity.Value) > 100)
                {
                    var reduceQty = new Quantity(Math.Abs(position.NetQuantity.Value) / 2);
                    return new Signal(
                        snapshot.CurrentTick.Symbol,
                        isLong ? OrderSide.Sell : OrderSide.Buy,
                        reduceQty,
                        SignalType.Reduce,
                        0.6,
                        "Momentum weakening - partial exit",
                        currentPrice
                    );
                }
            }

            await Task.CompletedTask;
            return null;
        }

        private Signal GenerateExitSignal(MarketSnapshot snapshot, PositionContext positionContext, string reason)
        {
            var position = positionContext.CurrentPosition!;
            var isLong = position.NetQuantity.Value > 0;
            var exitSide = isLong ? OrderSide.Sell : OrderSide.Buy;
            var exitQuantity = new Quantity(Math.Abs(position.NetQuantity.Value));

            _lastSignalPrice = snapshot.CurrentTick.MidPrice.Value;
            _lastSignalType = SignalType.Exit;

            return new Signal(
                snapshot.CurrentTick.Symbol,
                exitSide,
                exitQuantity,
                SignalType.Exit,
                0.9, // High confidence for exits
                reason,
                snapshot.CurrentTick.MidPrice
            );
        }

        private double CalculateConfidence(decimal momentum, bool isLong)
        {
            // Base confidence on momentum strength
            var momentumStrength = Math.Abs(momentum) / (_parameters.MomentumThreshold * 2);
            var baseConfidence = Math.Min(0.5 + (double)momentumStrength * 0.3, 0.95);

            // Adjust for momentum consistency
            if (_momentumHistory.Count >= 3)
            {
                var consistent = isLong
                    ? _momentumHistory.TakeLast(3).All(m => m > 0)
                    : _momentumHistory.TakeLast(3).All(m => m < 0);

                if (consistent)
                    baseConfidence += 0.1;
            }

            return Math.Min(baseConfidence, 1.0);
        }

        private Quantity CalculatePositionSize(PositionContext context, Price currentPrice)
        {
            // Calculate position size based on available capital and risk parameters
            var targetValue = context.AvailableCapital * (_parameters.PositionSizePercent / 100);

            // Check if this would exceed risk limits
            if (context.WouldExceedRiskLimits(new Quantity(1), currentPrice))
                return Quantity.Zero;

            var quantity = targetValue / currentPrice.Value;
            return new Quantity(Math.Floor(quantity)); // Round down to whole shares
        }

        private Price CalculateStopLoss(Price entryPrice, OrderSide side)
        {
            var stopDistance = entryPrice.Value * (_parameters.StopLossPercent / 100);

            if (side == OrderSide.Buy)
                return new Price(entryPrice.Value - stopDistance);
            else
                return new Price(entryPrice.Value + stopDistance);
        }

        private Price CalculateTakeProfit(Price entryPrice, OrderSide side)
        {
            var profitDistance = entryPrice.Value * (_parameters.TakeProfitPercent / 100);

            if (side == OrderSide.Buy)
                return new Price(entryPrice.Value + profitDistance);
            else
                return new Price(entryPrice.Value - profitDistance);
        }
    }
}