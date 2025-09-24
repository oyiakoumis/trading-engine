using TradingEngine.Execution.Commands;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Execution.Pipeline.Stages;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.Events;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace TradingEngine.Execution.Services
{
    /// <summary>
    /// Modern order processor implementing CQRS patterns
    /// Replaces the traditional OrderManager with command/query separation
    /// High-performance, event-sourced order lifecycle management
    /// </summary>
    public sealed class OrderProcessor : IOrderCommandHandler, IOrderTracker, IDisposable
    {
        private readonly IEventBus _eventBus;
        private readonly ILogger<OrderProcessor>? _logger;
        
        // High-performance order storage
        private readonly ConcurrentDictionary<OrderId, Order> _orders;
        private readonly ConcurrentDictionary<Symbol, ConcurrentBag<OrderId>> _ordersBySymbol;
        
        // Order state management
        private readonly OrderProcessorMetrics _metrics;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _processingLock;
        private bool _disposed;

        public event EventHandler<OrderStatusChangedEventArgs>? OrderStatusChanged;

        public OrderProcessor(
            IEventBus eventBus,
            ILogger<OrderProcessor>? logger = null)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger;

            _orders = new ConcurrentDictionary<OrderId, Order>();
            _ordersBySymbol = new ConcurrentDictionary<Symbol, ConcurrentBag<OrderId>>();
            _metrics = new OrderProcessorMetrics();
            _processingLock = new SemaphoreSlim(1, 1);

            // Cleanup old completed orders every 5 minutes
            _cleanupTimer = new Timer(
                _ => CleanupCompletedOrders(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        // CQRS Command Handlers
        public async ValueTask<CommandResult> HandleAsync<TCommand>(
            TCommand command,
            CancellationToken cancellationToken = default)
            where TCommand : IOrderCommand
        {
            return command switch
            {
                CreateOrderCommand createCmd => await HandleAsync(createCmd, cancellationToken),
                CancelOrderCommand cancelCmd => await HandleAsync(cancelCmd, cancellationToken),
                ModifyOrderCommand modifyCmd => await HandleAsync(modifyCmd, cancellationToken),
                _ => throw new NotSupportedException($"Command type {typeof(TCommand)} is not supported")
            };
        }

        public async ValueTask<CommandResult> HandleAsync(
            CreateOrderCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                return CommandResult.Failure("Command cannot be null", command.CommandId);

            try
            {
                // Create new order from command
                var order = CreateOrderFromCommand(command);
                
                // Store order
                if (!_orders.TryAdd(order.Id, order))
                {
                    return CommandResult.Failure($"Failed to store order {order.Id}", command.CommandId);
                }

                // Index by symbol
                _ordersBySymbol.AddOrUpdate(
                    order.Symbol,
                    new ConcurrentBag<OrderId> { order.Id },
                    (_, existing) =>
                    {
                        existing.Add(order.Id);
                        return existing;
                    });

                // Update metrics
                _metrics.IncrementOrdersCreated();

                // Publish domain events
                await PublishOrderCreatedEventAsync(order, command.CorrelationId);

                // Raise status changed event
                RaiseOrderStatusChanged(order, OrderStatus.Pending, order.Status);

                _logger?.LogDebug(
                    "Order {OrderId} created: {Symbol} {Side} {Quantity} @ {Price}",
                    order.Id,
                    order.Symbol,
                    order.Side,
                    order.Quantity,
                    order.LimitPrice?.ToString() ?? "MARKET");

                return CommandResult.Success(order.Id, command.CommandId, new Dictionary<string, object>
                {
                    ["Order"] = order,
                    ["CorrelationId"] = command.CorrelationId ?? string.Empty
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error creating order from command {CommandId}", command.CommandId);
                return CommandResult.Failure($"Order creation failed: {ex.Message}", command.CommandId);
            }
        }

        public async ValueTask<CommandResult> HandleAsync(
            CancelOrderCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                return CommandResult.Failure("Command cannot be null", command.CommandId);

            try
            {
                if (!_orders.TryGetValue(command.OrderId, out var order))
                {
                    return CommandResult.Failure($"Order {command.OrderId} not found", command.CommandId);
                }

                var oldStatus = order.Status;
                
                // Cancel the order
                order.Cancel(command.Reason);

                // Update metrics
                _metrics.IncrementOrdersCancelled();

                // Publish events
                await PublishOrderStatusChangedEventAsync(order, oldStatus, order.Status, command.CorrelationId);

                // Raise status changed event
                RaiseOrderStatusChanged(order, oldStatus, order.Status, command.Reason);

                _logger?.LogDebug("Order {OrderId} cancelled: {Reason}", order.Id, command.Reason);

                return CommandResult.Success(order.Id, command.CommandId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error cancelling order {OrderId}", command.OrderId);
                return CommandResult.Failure($"Order cancellation failed: {ex.Message}", command.CommandId);
            }
        }

        public async ValueTask<CommandResult> HandleAsync(
            ModifyOrderCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                return CommandResult.Failure("Command cannot be null", command.CommandId);

            try
            {
                if (!_orders.TryGetValue(command.OrderId, out var order))
                {
                    return CommandResult.Failure($"Order {command.OrderId} not found", command.CommandId);
                }

                var oldStatus = order.Status;

                // Modify the order
                order.Modify(command.NewQuantity, command.NewLimitPrice, command.NewStopPrice);

                // Update metrics
                _metrics.IncrementOrdersModified();

                // Publish events
                await PublishOrderModifiedEventAsync(order, command.CorrelationId);

                _logger?.LogDebug(
                    "Order {OrderId} modified: Qty={NewQuantity}, Price={NewPrice}",
                    order.Id,
                    command.NewQuantity,
                    command.NewLimitPrice);

                return CommandResult.Success(order.Id, command.CommandId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error modifying order {OrderId}", command.OrderId);
                return CommandResult.Failure($"Order modification failed: {ex.Message}", command.CommandId);
            }
        }

        public async ValueTask<IReadOnlyList<CommandResult>> HandleBatchAsync<TCommand>(
            IReadOnlyList<TCommand> commands,
            CancellationToken cancellationToken = default)
            where TCommand : IOrderCommand
        {
            var results = new List<CommandResult>(commands.Count);

            foreach (var command in commands)
            {
                try
                {
                    var result = await HandleAsync(command, cancellationToken);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing batch command {CommandId}", command.CommandId);
                    results.Add(CommandResult.Failure($"Batch processing failed: {ex.Message}", command.CommandId));
                }
            }

            return results;
        }

        public async ValueTask<bool> IsAvailableAsync()
        {
            // Simple availability check - in production this might check database connectivity, etc.
            await Task.CompletedTask;
            return !_disposed;
        }

        // Order Tracker Implementation (for ExecutionStage)
        public async ValueTask<Order?> GetOrderAsync(
            OrderId orderId,
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            return _orders.TryGetValue(orderId, out var order) ? order : null;
        }

        public async ValueTask TrackOrderAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            // This would be called when an order is created externally
            // For our implementation, orders are created through commands
            await Task.CompletedTask;
        }

        public async ValueTask UpdateOrderAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            // Update order state (e.g., from exchange fills)
            if (_orders.ContainsKey(order.Id))
            {
                _orders[order.Id] = order;
                await PublishOrderStatusChangedEventAsync(order, OrderStatus.Pending, order.Status);
            }
        }

        // Order Status Updates (called by exchange)
        public async Task ProcessOrderFillAsync(OrderId orderId, Quantity fillQuantity, Price fillPrice, decimal commission = 0)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                var oldStatus = order.Status;
                order.Fill(fillQuantity, fillPrice, commission);

                _metrics.IncrementOrdersFilled();

                await PublishOrderStatusChangedEventAsync(order, oldStatus, order.Status);
                RaiseOrderStatusChanged(order, oldStatus, order.Status);

                _logger?.LogInformation(
                    "Order {OrderId} filled: {FillQuantity} @ {FillPrice} (Total: {TotalFilled}/{TotalQuantity})",
                    orderId,
                    fillQuantity,
                    fillPrice,
                    order.FilledQuantity,
                    order.Quantity);
            }
        }

        public async Task ProcessOrderRejectionAsync(OrderId orderId, string reason)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                var oldStatus = order.Status;
                order.Reject(reason);

                _metrics.IncrementOrdersRejected();

                await PublishOrderStatusChangedEventAsync(order, oldStatus, order.Status);
                RaiseOrderStatusChanged(order, oldStatus, order.Status, reason);

                _logger?.LogWarning("Order {OrderId} rejected: {Reason}", orderId, reason);
            }
        }

        // Query Operations
        public IEnumerable<Order> GetActiveOrders(Symbol? symbol = null)
        {
            var activeOrders = _orders.Values.Where(o => o.IsActive);
            
            if (symbol.HasValue)
            {
                activeOrders = activeOrders.Where(o => o.Symbol == symbol.Value);
            }

            return activeOrders.ToList();
        }

        public IEnumerable<Order> GetOrdersBySymbol(Symbol symbol)
        {
            if (_ordersBySymbol.TryGetValue(symbol, out var orderIds))
            {
                return orderIds
                    .Select(id => _orders.TryGetValue(id, out var order) ? order : null)
                    .Where(order => order != null)
                    .Cast<Order>()
                    .ToList();
            }

            return Enumerable.Empty<Order>();
        }

        public OrderProcessorMetrics GetMetrics() => _metrics.GetSnapshot();

        // Private Helper Methods
        private Order CreateOrderFromCommand(CreateOrderCommand command)
        {
            return new Order(
                command.Symbol,
                command.Side,
                command.OrderType,
                command.Quantity,
                command.LimitPrice,
                command.StopPrice,
                command.ClientId,
                command.Tag);
        }

        private async Task PublishOrderCreatedEventAsync(Order order, string? correlationId)
        {
            var @event = new OrderCreatedDomainEvent
            {
                OrderId = order.Id,
                Symbol = order.Symbol,
                Side = order.Side,
                OrderType = order.Type.ToString(),
                Quantity = order.Quantity,
                LimitPrice = order.LimitPrice,
                StopPrice = order.StopPrice,
                Status = order.Status,
                CreatedAt = order.CreatedAt,
                CorrelationId = correlationId
            };

            await _eventBus.PublishAsync(@event);
        }

        private async Task PublishOrderStatusChangedEventAsync(
            Order order, 
            OrderStatus oldStatus, 
            OrderStatus newStatus, 
            string? correlationId = null)
        {
            var @event = new OrderStatusChangedDomainEvent
            {
                OrderId = order.Id,
                Symbol = order.Symbol,
                OldStatus = oldStatus,
                NewStatus = newStatus,
                FilledQuantity = order.FilledQuantity,
                RemainingQuantity = order.RemainingQuantity,
                AverageFillPrice = order.AverageFillPrice,
                UpdatedAt = order.UpdatedAt ?? Timestamp.Now,
                CorrelationId = correlationId
            };

            await _eventBus.PublishAsync(@event);
        }

        private async Task PublishOrderModifiedEventAsync(Order order, string? correlationId)
        {
            var @event = new OrderModifiedDomainEvent
            {
                OrderId = order.Id,
                Symbol = order.Symbol,
                Quantity = order.Quantity,
                LimitPrice = order.LimitPrice,
                StopPrice = order.StopPrice,
                ModifiedAt = order.UpdatedAt ?? Timestamp.Now,
                CorrelationId = correlationId
            };

            await _eventBus.PublishAsync(@event);
        }

        private void RaiseOrderStatusChanged(Order order, OrderStatus oldStatus, OrderStatus newStatus, string? reason = null)
        {
            try
            {
                var args = new OrderStatusChangedEventArgs(order, oldStatus, newStatus, reason);
                OrderStatusChanged?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising OrderStatusChanged event");
            }
        }

        private void CleanupCompletedOrders()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddHours(-24);
                var completedOrders = _orders.Values
                    .Where(o => o.IsComplete && o.UpdatedAt?.Value < cutoffTime)
                    .Select(o => o.Id)
                    .ToList();

                foreach (var orderId in completedOrders)
                {
                    if (_orders.TryRemove(orderId, out var order))
                    {
                        // Also remove from symbol index
                        if (_ordersBySymbol.TryGetValue(order.Symbol, out var symbolOrders))
                        {
                            // Note: ConcurrentBag doesn't have a remove method
                            // In production, consider using a different data structure
                            // or implementing a more sophisticated cleanup strategy
                        }
                    }
                }

                if (completedOrders.Count > 0)
                {
                    _logger?.LogDebug("Cleaned up {Count} completed orders", completedOrders.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during order cleanup");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _cleanupTimer?.Dispose();
                _processingLock?.Dispose();

                _logger?.LogInformation("Order processor disposed");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during order processor disposal");
            }
        }
    }

    /// <summary>
    /// Metrics for the order processor
    /// </summary>
    public sealed class OrderProcessorMetrics
    {
        private long _ordersCreated;
        private long _ordersCancelled;
        private long _ordersModified;
        private long _ordersFilled;
        private long _ordersRejected;
        private readonly DateTime _startTime = DateTime.UtcNow;

        public long OrdersCreated => _ordersCreated;
        public long OrdersCancelled => _ordersCancelled;
        public long OrdersModified => _ordersModified;
        public long OrdersFilled => _ordersFilled;
        public long OrdersRejected => _ordersRejected;
        public long TotalOrders => _ordersCreated;
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;

        public void IncrementOrdersCreated() => Interlocked.Increment(ref _ordersCreated);
        public void IncrementOrdersCancelled() => Interlocked.Increment(ref _ordersCancelled);
        public void IncrementOrdersModified() => Interlocked.Increment(ref _ordersModified);
        public void IncrementOrdersFilled() => Interlocked.Increment(ref _ordersFilled);
        public void IncrementOrdersRejected() => Interlocked.Increment(ref _ordersRejected);

        public OrderProcessorMetrics GetSnapshot()
        {
            return new OrderProcessorMetrics
            {
                _ordersCreated = this._ordersCreated,
                _ordersCancelled = this._ordersCancelled,
                _ordersModified = this._ordersModified,
                _ordersFilled = this._ordersFilled,
                _ordersRejected = this._ordersRejected
            };
        }
    }

    // Domain Events
    public sealed class OrderCreatedDomainEvent : IEvent
    {
        public string EventId { get; } = Guid.NewGuid().ToString("N");
        public string EventType { get; } = nameof(OrderCreatedDomainEvent);
        public Timestamp Timestamp { get; } = Timestamp.Now;

        public OrderId OrderId { get; set; }
        public Symbol Symbol { get; set; }
        public OrderSide Side { get; set; }
        public string OrderType { get; set; } = string.Empty;
        public Quantity Quantity { get; set; }
        public Price? LimitPrice { get; set; }
        public Price? StopPrice { get; set; }
        public OrderStatus Status { get; set; }
        public Timestamp CreatedAt { get; set; }
        public string? CorrelationId { get; set; }
    }

    public sealed class OrderStatusChangedDomainEvent : IEvent
    {
        public string EventId { get; } = Guid.NewGuid().ToString("N");
        public string EventType { get; } = nameof(OrderStatusChangedDomainEvent);
        public Timestamp Timestamp { get; } = Timestamp.Now;

        public OrderId OrderId { get; set; }
        public Symbol Symbol { get; set; }
        public OrderStatus OldStatus { get; set; }
        public OrderStatus NewStatus { get; set; }
        public Quantity FilledQuantity { get; set; }
        public Quantity RemainingQuantity { get; set; }
        public Price AverageFillPrice { get; set; }
        public Timestamp UpdatedAt { get; set; }
        public string? CorrelationId { get; set; }
    }

    public sealed class OrderModifiedDomainEvent : IEvent
    {
        public string EventId { get; } = Guid.NewGuid().ToString("N");
        public string EventType { get; } = nameof(OrderModifiedDomainEvent);
        public Timestamp Timestamp { get; } = Timestamp.Now;

        public OrderId OrderId { get; set; }
        public Symbol Symbol { get; set; }
        public Quantity Quantity { get; set; }
        public Price? LimitPrice { get; set; }
        public Price? StopPrice { get; set; }
        public Timestamp ModifiedAt { get; set; }
        public string? CorrelationId { get; set; }
    }
}