using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Execution.Commands
{
    /// <summary>
    /// Base interface for order commands
    /// Command pattern for order operations
    /// </summary>
    public interface IOrderCommand
    {
        /// <summary>
        /// Unique command identifier
        /// </summary>
        string CommandId { get; }
        
        /// <summary>
        /// Correlation ID for tracing
        /// </summary>
        string? CorrelationId { get; }
        
        /// <summary>
        /// Command timestamp
        /// </summary>
        DateTime CreatedAt { get; }
    }

    /// <summary>
    /// Command to create a new order
    /// </summary>
    public sealed class CreateOrderCommand : IOrderCommand
    {
        public string CommandId { get; } = Guid.NewGuid().ToString("N");
        public string? CorrelationId { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        
        public Symbol Symbol { get; set; }
        public OrderSide Side { get; set; }
        public OrderType OrderType { get; set; }
        public Quantity Quantity { get; set; }
        public Price? LimitPrice { get; set; }
        public Price? StopPrice { get; set; }
        public string? ClientId { get; set; }
        public string? Tag { get; set; }
        public OrderId? ParentOrderId { get; set; }
    }

    /// <summary>
    /// Command to cancel an existing order
    /// </summary>
    public sealed class CancelOrderCommand : IOrderCommand
    {
        public string CommandId { get; } = Guid.NewGuid().ToString("N");
        public string? CorrelationId { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        
        public OrderId OrderId { get; set; }
        public string Reason { get; set; } = "User requested";
    }

    /// <summary>
    /// Command to modify an existing order
    /// </summary>
    public sealed class ModifyOrderCommand : IOrderCommand
    {
        public string CommandId { get; } = Guid.NewGuid().ToString("N");
        public string? CorrelationId { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        
        public OrderId OrderId { get; set; }
        public Quantity? NewQuantity { get; set; }
        public Price? NewLimitPrice { get; set; }
        public Price? NewStopPrice { get; set; }
    }

    /// <summary>
    /// Result of command execution
    /// </summary>
    public sealed record CommandResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public OrderId? OrderId { get; init; }
        public string? CommandId { get; init; }
        public DateTime ProcessedAt { get; init; } = DateTime.UtcNow;
        public IReadOnlyDictionary<string, object> Data { get; init; } = 
            new Dictionary<string, object>();

        public static CommandResult Success(
            OrderId orderId,
            string? commandId = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new CommandResult
            {
                IsSuccess = true,
                OrderId = orderId,
                CommandId = commandId,
                Data = data ?? new Dictionary<string, object>()
            };
        }

        public static CommandResult Failure(
            string errorMessage,
            string? commandId = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new CommandResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                CommandId = commandId,
                Data = data ?? new Dictionary<string, object>()
            };
        }
    }

    /// <summary>
    /// Interface for order command handler
    /// CQRS write side for order operations
    /// </summary>
    public interface IOrderCommandHandler
    {
        /// <summary>
        /// Handle a single order command
        /// </summary>
        ValueTask<CommandResult> HandleAsync<TCommand>(
            TCommand command,
            CancellationToken cancellationToken = default)
            where TCommand : IOrderCommand;

        /// <summary>
        /// Handle a create order command specifically
        /// </summary>
        ValueTask<CommandResult> HandleAsync(
            CreateOrderCommand command,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Handle a cancel order command specifically
        /// </summary>
        ValueTask<CommandResult> HandleAsync(
            CancelOrderCommand command,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Handle a modify order command specifically
        /// </summary>
        ValueTask<CommandResult> HandleAsync(
            ModifyOrderCommand command,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Handle multiple commands in a batch
        /// </summary>
        ValueTask<IReadOnlyList<CommandResult>> HandleBatchAsync<TCommand>(
            IReadOnlyList<TCommand> commands,
            CancellationToken cancellationToken = default)
            where TCommand : IOrderCommand;

        /// <summary>
        /// Check if command handler is available
        /// </summary>
        ValueTask<bool> IsAvailableAsync();
    }
}