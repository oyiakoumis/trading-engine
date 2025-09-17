using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Domain.Events
{
    /// <summary>
    /// Event raised when a new tick is received
    /// </summary>
    public class TickReceivedEvent : EventBase
    {
        public Tick Tick { get; }

        public TickReceivedEvent(Tick tick)
        {
            Tick = tick;
        }
    }

    /// <summary>
    /// Event raised when a trading signal is generated
    /// </summary>
    public class SignalGeneratedEvent : EventBase
    {
        public Symbol Symbol { get; }
        public OrderSide Side { get; }
        public Quantity Quantity { get; }
        public string SignalType { get; }
        public double Confidence { get; }
        public string Reason { get; }

        public SignalGeneratedEvent(
            Symbol symbol,
            OrderSide side,
            Quantity quantity,
            string signalType,
            double confidence,
            string reason)
        {
            Symbol = symbol;
            Side = side;
            Quantity = quantity;
            SignalType = signalType;
            Confidence = confidence;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event raised when an order is placed
    /// </summary>
    public class OrderPlacedEvent : EventBase
    {
        public Order Order { get; }

        public OrderPlacedEvent(Order order)
        {
            Order = order;
        }
    }

    /// <summary>
    /// Event raised when an order is submitted to exchange
    /// </summary>
    public class OrderSubmittedEvent : EventBase
    {
        public OrderId OrderId { get; }
        public Symbol Symbol { get; }

        public OrderSubmittedEvent(OrderId orderId, Symbol symbol)
        {
            OrderId = orderId;
            Symbol = symbol;
        }
    }

    /// <summary>
    /// Event raised when an order is filled/executed
    /// </summary>
    public class OrderExecutedEvent : EventBase
    {
        public OrderId OrderId { get; }
        public Trade Trade { get; }
        public bool IsFullyFilled { get; }

        public OrderExecutedEvent(OrderId orderId, Trade trade, bool isFullyFilled)
        {
            OrderId = orderId;
            Trade = trade;
            IsFullyFilled = isFullyFilled;
        }
    }

    /// <summary>
    /// Event raised when an order is cancelled
    /// </summary>
    public class OrderCancelledEvent : EventBase
    {
        public OrderId OrderId { get; }
        public string Reason { get; }

        public OrderCancelledEvent(OrderId orderId, string reason)
        {
            OrderId = orderId;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event raised when an order is rejected
    /// </summary>
    public class OrderRejectedEvent : EventBase
    {
        public OrderId OrderId { get; }
        public string Reason { get; }

        public OrderRejectedEvent(OrderId orderId, string reason)
        {
            OrderId = orderId;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event raised when a position is opened
    /// </summary>
    public class PositionOpenedEvent : EventBase
    {
        public string PositionId { get; }
        public Symbol Symbol { get; }
        public OrderSide Side { get; }
        public Quantity Quantity { get; }
        public Price EntryPrice { get; }

        public PositionOpenedEvent(
            string positionId,
            Symbol symbol,
            OrderSide side,
            Quantity quantity,
            Price entryPrice)
        {
            PositionId = positionId;
            Symbol = symbol;
            Side = side;
            Quantity = quantity;
            EntryPrice = entryPrice;
        }
    }

    /// <summary>
    /// Event raised when a position is closed
    /// </summary>
    public class PositionClosedEvent : EventBase
    {
        public string PositionId { get; }
        public Symbol Symbol { get; }
        public decimal RealizedPnL { get; }
        public decimal TotalCommission { get; }

        public PositionClosedEvent(
            string positionId,
            Symbol symbol,
            decimal realizedPnL,
            decimal totalCommission)
        {
            PositionId = positionId;
            Symbol = symbol;
            RealizedPnL = realizedPnL;
            TotalCommission = totalCommission;
        }
    }

    /// <summary>
    /// Event raised when a risk limit is breached
    /// </summary>
    public class RiskLimitBreachedEvent : EventBase
    {
        public string RiskType { get; }
        public string Description { get; }
        public decimal CurrentValue { get; }
        public decimal LimitValue { get; }
        public string Action { get; }

        public RiskLimitBreachedEvent(
            string riskType,
            string description,
            decimal currentValue,
            decimal limitValue,
            string action)
        {
            RiskType = riskType;
            Description = description;
            CurrentValue = currentValue;
            LimitValue = limitValue;
            Action = action;
        }
    }

    /// <summary>
    /// Event raised when P&L is updated
    /// </summary>
    public class PnLUpdatedEvent : EventBase
    {
        public Symbol Symbol { get; }
        public decimal RealizedPnL { get; }
        public decimal UnrealizedPnL { get; }
        public decimal TotalPnL { get; }
        public decimal DailyPnL { get; }

        public PnLUpdatedEvent(
            Symbol symbol,
            decimal realizedPnL,
            decimal unrealizedPnL,
            decimal totalPnL,
            decimal dailyPnL)
        {
            Symbol = symbol;
            RealizedPnL = realizedPnL;
            UnrealizedPnL = unrealizedPnL;
            TotalPnL = totalPnL;
            DailyPnL = dailyPnL;
        }
    }

    /// <summary>
    /// Event raised for system-wide notifications
    /// </summary>
    public class SystemEvent : EventBase
    {
        public string Level { get; }
        public string Message { get; }
        public string Component { get; }

        public SystemEvent(string level, string message, string component)
        {
            Level = level;
            Message = message;
            Component = component;
        }
    }
}