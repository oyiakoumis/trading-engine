using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Domain.Events
{
    /// <summary>
    /// Base interface for all domain events
    /// </summary>
    public interface IEvent
    {
        Timestamp Timestamp { get; }
        string EventId { get; }
        string EventType { get; }
    }

    /// <summary>
    /// Base class for domain events
    /// </summary>
    public abstract class EventBase : IEvent
    {
        public Timestamp Timestamp { get; }
        public string EventId { get; }
        public string EventType => GetType().Name;

        protected EventBase()
        {
            Timestamp = Timestamp.Now;
            EventId = Guid.NewGuid().ToString("N");
        }
    }
}