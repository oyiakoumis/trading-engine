using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Execution.Pipeline.Stages
{
    /// <summary>
    /// Signal and order parameter validation stage
    /// Fast-fail with detailed error messages and high performance validation
    /// Uses Span<T> for minimal allocations where possible
    /// </summary>
    public sealed class ValidationStage : OrderProcessingStageBase
    {
        public override string StageName => "Validation";
        public override int Priority => 100; // First stage to execute

        protected override ValueTask<StageResult> ProcessInternalAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken)
        {
            var validationErrors = new List<string>();
            var signal = context.Signal;

            // Basic signal validation
            if (signal == null)
            {
                return ValueTask.FromResult(StageResult.Failed("Signal is null"));
            }

            // Validate signal properties
            ValidateSignalProperties(signal, validationErrors);
            ValidateQuantity(signal.Quantity, validationErrors);
            ValidatePrices(signal, validationErrors);
            ValidateTimestamp(signal, validationErrors);
            ValidateConfidence(signal, validationErrors);

            if (validationErrors.Count > 0)
            {
                return ValueTask.FromResult(StageResult.Failed(
                    $"Signal validation failed: {string.Join("; ", validationErrors)}"));
            }

            return ValueTask.FromResult(StageResult.Success());
        }

        private static void ValidateSignalProperties(Signal signal, List<string> errors)
        {
            // Symbol validation
            if (string.IsNullOrWhiteSpace(signal.Symbol.Value))
            {
                errors.Add("Symbol cannot be null or empty");
            }

            // Reason validation
            if (string.IsNullOrWhiteSpace(signal.Reason))
            {
                errors.Add("Signal reason cannot be null or empty");
            }

            // Signal type validation
            if (!Enum.IsDefined(typeof(SignalType), signal.Type))
            {
                errors.Add($"Invalid signal type: {signal.Type}");
            }

            // Side validation
            if (!Enum.IsDefined(typeof(Domain.Enums.OrderSide), signal.Side))
            {
                errors.Add($"Invalid order side: {signal.Side}");
            }
        }

        private static void ValidateQuantity(Domain.ValueObjects.Quantity quantity, List<string> errors)
        {
            if (quantity.IsZero)
            {
                errors.Add("Quantity cannot be zero");
            }

            if (quantity.Value < 0)
            {
                errors.Add("Quantity cannot be negative");
            }

            // Check for reasonable quantity limits (prevent obvious errors)
            if (quantity.Value > 1_000_000)
            {
                errors.Add("Quantity exceeds maximum allowed limit (1,000,000)");
            }

            if (quantity.Value != Math.Floor(quantity.Value) && quantity.Value < 1)
            {
                errors.Add("Fractional quantities below 1 are not supported");
            }
        }

        private static void ValidatePrices(Signal signal, List<string> errors)
        {
            // Target price validation
            if (signal.TargetPrice.HasValue)
            {
                var price = signal.TargetPrice.Value;
                if (price.Value <= 0)
                {
                    errors.Add("Target price must be positive");
                }

                if (price.Value > 1_000_000)
                {
                    errors.Add("Target price exceeds maximum allowed limit ($1,000,000)");
                }
            }

            // Stop loss validation
            if (signal.StopLoss.HasValue)
            {
                var stopLoss = signal.StopLoss.Value;
                if (stopLoss.Value <= 0)
                {
                    errors.Add("Stop loss price must be positive");
                }

                // Validate stop loss makes sense relative to target price and side
                if (signal.TargetPrice.HasValue)
                {
                    ValidateStopLossLogic(signal, stopLoss, errors);
                }
            }

            // Take profit validation
            if (signal.TakeProfit.HasValue)
            {
                var takeProfit = signal.TakeProfit.Value;
                if (takeProfit.Value <= 0)
                {
                    errors.Add("Take profit price must be positive");
                }

                // Validate take profit makes sense relative to target price and side
                if (signal.TargetPrice.HasValue)
                {
                    ValidateTakeProfitLogic(signal, takeProfit, errors);
                }
            }

            // Validate relationship between stop loss and take profit
            if (signal.StopLoss.HasValue && signal.TakeProfit.HasValue)
            {
                ValidateStopLossTakeProfitRelationship(signal, errors);
            }
        }

        private static void ValidateStopLossLogic(Signal signal, Domain.ValueObjects.Price stopLoss, List<string> errors)
        {
            var targetPrice = signal.TargetPrice!.Value;
            
            switch (signal.Side)
            {
                case Domain.Enums.OrderSide.Buy:
                    // For buy orders, stop loss should be below target price
                    if (stopLoss.Value >= targetPrice.Value)
                    {
                        errors.Add("For buy orders, stop loss must be below target price");
                    }
                    break;

                case Domain.Enums.OrderSide.Sell:
                    // For sell orders, stop loss should be above target price
                    if (stopLoss.Value <= targetPrice.Value)
                    {
                        errors.Add("For sell orders, stop loss must be above target price");
                    }
                    break;
            }
        }

        private static void ValidateTakeProfitLogic(Signal signal, Domain.ValueObjects.Price takeProfit, List<string> errors)
        {
            var targetPrice = signal.TargetPrice!.Value;
            
            switch (signal.Side)
            {
                case Domain.Enums.OrderSide.Buy:
                    // For buy orders, take profit should be above target price
                    if (takeProfit.Value <= targetPrice.Value)
                    {
                        errors.Add("For buy orders, take profit must be above target price");
                    }
                    break;

                case Domain.Enums.OrderSide.Sell:
                    // For sell orders, take profit should be below target price
                    if (takeProfit.Value >= targetPrice.Value)
                    {
                        errors.Add("For sell orders, take profit must be below target price");
                    }
                    break;
            }
        }

        private static void ValidateStopLossTakeProfitRelationship(Signal signal, List<string> errors)
        {
            var stopLoss = signal.StopLoss!.Value;
            var takeProfit = signal.TakeProfit!.Value;

            switch (signal.Side)
            {
                case Domain.Enums.OrderSide.Buy:
                    // For buy orders: stop loss < target price < take profit
                    if (stopLoss.Value >= takeProfit.Value)
                    {
                        errors.Add("For buy orders, stop loss must be below take profit");
                    }
                    break;

                case Domain.Enums.OrderSide.Sell:
                    // For sell orders: take profit < target price < stop loss
                    if (takeProfit.Value >= stopLoss.Value)
                    {
                        errors.Add("For sell orders, take profit must be below stop loss");
                    }
                    break;
            }
        }

        private static void ValidateTimestamp(Signal signal, List<string> errors)
        {
            var now = DateTime.UtcNow;
            var signalTime = signal.GeneratedAt.Value;

            // Check if signal is too old (more than 1 minute)
            if (now - signalTime > TimeSpan.FromMinutes(1))
            {
                errors.Add("Signal is too old (generated more than 1 minute ago)");
            }

            // Check if signal is from the future (allow small clock skew)
            if (signalTime > now.AddSeconds(30))
            {
                errors.Add("Signal timestamp is in the future");
            }
        }

        private static void ValidateConfidence(Signal signal, List<string> errors)
        {
            if (signal.Confidence < 0.0 || signal.Confidence > 1.0)
            {
                errors.Add("Signal confidence must be between 0.0 and 1.0");
            }

            // Warn about very low confidence signals
            if (signal.Confidence < 0.1)
            {
                errors.Add("Signal confidence is extremely low (< 0.1)");
            }
        }

        public override ValueTask<bool> CanProcessAsync(OrderProcessingContext context)
        {
            // ValidationStage can process any context
            return ValueTask.FromResult(context?.Signal != null);
        }

        public override ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            // ValidationStage has no external dependencies, so it's always healthy
            var metrics = GetMetrics();
            var healthData = new Dictionary<string, object>
            {
                ["ExecutionCount"] = metrics.ExecutionCount,
                ["SuccessRate"] = metrics.SuccessRate,
                ["AverageExecutionTime"] = metrics.AverageExecutionTimeMs
            };

            return ValueTask.FromResult(HealthCheckResult.Healthy(
                $"ValidationStage is healthy. Processed {metrics.ExecutionCount} signals.",
                data: healthData));
        }
    }
}