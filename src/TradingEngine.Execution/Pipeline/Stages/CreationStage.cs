using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Execution.Commands;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Execution.Pipeline.Stages
{
    /// <summary>
    /// Order creation stage with command pattern
    /// Handles order creation and bracket order management
    /// Uses command pattern for order lifecycle operations
    /// </summary>
    public sealed class CreationStage : OrderProcessingStageBase
    {
        private readonly IOrderCommandHandler _commandHandler;
        private readonly ISignalToOrderConverter _converter;
        private readonly ILogger<CreationStage>? _logger;

        public override string StageName => "Creation";
        public override int Priority => 300; // Third stage after validation and risk assessment

        public CreationStage(
            IOrderCommandHandler commandHandler,
            ISignalToOrderConverter converter,
            ILogger<CreationStage>? logger = null)
        {
            _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
            _converter = converter ?? throw new ArgumentNullException(nameof(converter));
            _logger = logger;
        }

        protected override async ValueTask<StageResult> ProcessInternalAsync(
            OrderProcessingContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                // Convert signal to order command using strategy pattern
                var createOrderCommand = await _converter.ConvertAsync(
                    context.Signal, 
                    context.RiskLevel, 
                    context.CorrelationId,
                    cancellationToken);

                // Execute command through CQRS handler
                var result = await _commandHandler.HandleAsync(createOrderCommand, cancellationToken);

                if (!result.IsSuccess)
                {
                    _logger?.LogWarning(
                        "Order creation failed for signal {CorrelationId}: {ErrorMessage}",
                        context.CorrelationId,
                        result.ErrorMessage);

                    return StageResult.Failed($"Order creation failed: {result.ErrorMessage}");
                }

                _logger?.LogDebug(
                    "Order {OrderId} created successfully for signal {CorrelationId}",
                    result.OrderId,
                    context.CorrelationId);

                // Store order information in context
                var stageData = new Dictionary<string, object>
                {
                    ["OrderId"] = result.OrderId!,
                    ["OrderType"] = createOrderCommand.OrderType.ToString(),
                    ["CreatedAt"] = DateTime.UtcNow
                };

                // Handle bracket orders asynchronously if needed
                if (context.Signal.TakeProfit.HasValue || context.Signal.StopLoss.HasValue)
                {
                    var orderId = result.OrderId ?? throw new InvalidOperationException("OrderId cannot be null for successful result");
                    _ = Task.Run(() => CreateBracketOrdersAsync(context, orderId, cancellationToken),
                        cancellationToken);
                }

                return StageResult.Success(data: stageData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Unexpected error during order creation for signal {CorrelationId}",
                    context.CorrelationId);

                return StageResult.Failed($"Order creation error: {ex.Message}");
            }
        }

        private async Task CreateBracketOrdersAsync(
            OrderProcessingContext context, 
            OrderId parentOrderId, 
            CancellationToken cancellationToken)
        {
            try
            {
                var bracketCommands = new List<IOrderCommand>();

                // Create take profit order
                if (context.Signal.TakeProfit.HasValue)
                {
                    var tpCommand = CreateTakeProfitCommand(context, parentOrderId);
                    bracketCommands.Add(tpCommand);
                }

                // Create stop loss order
                if (context.Signal.StopLoss.HasValue)
                {
                    var slCommand = CreateStopLossCommand(context, parentOrderId);
                    bracketCommands.Add(slCommand);
                }

                // Execute bracket commands
                foreach (var command in bracketCommands)
                {
                    var result = await _commandHandler.HandleAsync(command, cancellationToken);
                    if (!result.IsSuccess)
                    {
                        _logger?.LogWarning(
                            "Bracket order creation failed for parent order {ParentOrderId}: {ErrorMessage}",
                            parentOrderId,
                            result.ErrorMessage);
                    }
                    else
                    {
                        _logger?.LogDebug(
                            "Bracket order {BracketOrderId} created for parent order {ParentOrderId}",
                            result.OrderId,
                            parentOrderId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Error creating bracket orders for parent order {ParentOrderId}",
                    parentOrderId);
            }
        }

        private CreateOrderCommand CreateTakeProfitCommand(OrderProcessingContext context, OrderId parentOrderId)
        {
            var oppositeSide = context.Signal.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            
            return new CreateOrderCommand
            {
                Symbol = context.Signal.Symbol,
                Side = oppositeSide,
                OrderType = OrderType.Limit,
                Quantity = context.Signal.Quantity,
                LimitPrice = context.Signal.TakeProfit,
                ClientId = $"TP_{parentOrderId.ToShortString()}",
                Tag = "TakeProfit",
                ParentOrderId = parentOrderId,
                CorrelationId = context.CorrelationId
            };
        }

        private CreateOrderCommand CreateStopLossCommand(OrderProcessingContext context, OrderId parentOrderId)
        {
            var oppositeSide = context.Signal.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            
            return new CreateOrderCommand
            {
                Symbol = context.Signal.Symbol,
                Side = oppositeSide,
                OrderType = OrderType.Stop,
                Quantity = context.Signal.Quantity,
                StopPrice = context.Signal.StopLoss,
                ClientId = $"SL_{parentOrderId.ToShortString()}",
                Tag = "StopLoss",
                ParentOrderId = parentOrderId,
                CorrelationId = context.CorrelationId
            };
        }

        public override async ValueTask<bool> CanProcessAsync(OrderProcessingContext context)
        {
            // Can only process if we have a valid signal and no existing order ID
            if (context?.Signal == null || context.OrderId != null)
                return false;

            // Check if command handler is available
            return await _commandHandler.IsAvailableAsync();
        }

        public override async ValueTask<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                
                // Check command handler availability
                var isAvailable = await _commandHandler.IsAvailableAsync();
                var responseTime = DateTime.UtcNow - startTime;
                var metrics = GetMetrics();

                var healthData = new Dictionary<string, object>
                {
                    ["CommandHandlerAvailable"] = isAvailable,
                    ["ExecutionCount"] = metrics.ExecutionCount,
                    ["SuccessRate"] = metrics.SuccessRate,
                    ["AverageExecutionTime"] = metrics.AverageExecutionTimeMs
                };

                if (!isAvailable)
                {
                    return HealthCheckResult.Unhealthy(
                        "Order command handler is unavailable",
                        responseTime,
                        healthData);
                }

                return HealthCheckResult.Healthy(
                    "Creation stage is healthy",
                    responseTime,
                    healthData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Health check failed for CreationStage");
                
                return HealthCheckResult.Unhealthy(
                    $"Health check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Interface for converting signals to order commands
    /// Strategy pattern implementation for different conversion strategies
    /// </summary>
    public interface ISignalToOrderConverter
    {
        /// <summary>
        /// Convert signal to create order command
        /// </summary>
        ValueTask<CreateOrderCommand> ConvertAsync(
            Strategies.Models.Signal signal,
            RiskLevel riskLevel,
            string? correlationId = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Default signal to order converter implementation
    /// </summary>
    public sealed class SignalToOrderConverter : ISignalToOrderConverter
    {
        private readonly ILogger<SignalToOrderConverter>? _logger;

        public SignalToOrderConverter(ILogger<SignalToOrderConverter>? logger = null)
        {
            _logger = logger;
        }

        public ValueTask<CreateOrderCommand> ConvertAsync(
            Strategies.Models.Signal signal,
            RiskLevel riskLevel,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            if (signal == null)
                throw new ArgumentNullException(nameof(signal));

            // Determine order type based on signal
            var orderType = signal.TargetPrice.HasValue ? OrderType.Limit : OrderType.Market;

            // Apply risk-based adjustments
            var adjustedQuantity = ApplyRiskAdjustments(signal.Quantity, riskLevel);

            var command = new CreateOrderCommand
            {
                Symbol = signal.Symbol,
                Side = signal.Side,
                OrderType = orderType,
                Quantity = adjustedQuantity,
                LimitPrice = signal.TargetPrice,
                StopPrice = signal.StopLoss,
                ClientId = $"Signal_{signal.GeneratedAt.UnixMilliseconds}",
                Tag = $"Strategy_{signal.Reason}",
                CorrelationId = correlationId
            };

            _logger?.LogDebug(
                "Converted signal to {OrderType} order for {Symbol} {Side} {Quantity} @ {Price}",
                orderType,
                signal.Symbol,
                signal.Side,
                adjustedQuantity,
                signal.TargetPrice?.ToString() ?? "MARKET");

            return ValueTask.FromResult(command);
        }

        private Quantity ApplyRiskAdjustments(Quantity originalQuantity, RiskLevel riskLevel)
        {
            // Apply quantity adjustments based on risk level
            var adjustmentFactor = riskLevel switch
            {
                RiskLevel.Low => 1.0m,
                RiskLevel.Medium => 0.8m,
                RiskLevel.High => 0.5m,
                RiskLevel.Critical => 0.2m,
                _ => 1.0m
            };

            var adjustedValue = Math.Max(1, Math.Floor(originalQuantity.Value * adjustmentFactor));
            return new Quantity(adjustedValue);
        }
    }
}