using TradingEngine.Domain.Events;

namespace TradingEngine.Infrastructure.EventBus
{
    /// <summary>
    /// Interface for event bus implementation
    /// </summary>
    public interface IEventBus
    {
        /// <summary>
        /// Publish an event asynchronously
        /// </summary>
        Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IEvent;

        /// <summary>
        /// Subscribe to an event type
        /// </summary>
        IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler)
            where TEvent : IEvent;

        /// <summary>
        /// Subscribe to an event type with filter
        /// </summary>
        IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler, Func<TEvent, bool> filter)
            where TEvent : IEvent;

        /// <summary>
        /// Subscribe to all events
        /// </summary>
        IDisposable SubscribeAll(Func<IEvent, Task> handler);

        /// <summary>
        /// Get statistics about event processing
        /// </summary>
        EventBusStatistics GetStatistics();
    }

    /// <summary>
    /// Event bus statistics
    /// </summary>
    public class EventBusStatistics
    {
        public long TotalEventsPublished { get; set; }
        public long TotalEventsProcessed { get; set; }
        public long TotalEventsFailed { get; set; }
        public int ActiveSubscriptions { get; set; }
        public int QueuedEvents { get; set; }
        public TimeSpan AverageProcessingTime { get; set; }
        public DateTime LastEventTime { get; set; }

        public override string ToString()
        {
            return $"EventBus Stats: Published={TotalEventsPublished}, Processed={TotalEventsProcessed}, " +
                   $"Failed={TotalEventsFailed}, Active Subs={ActiveSubscriptions}, " +
                   $"Queued={QueuedEvents}, Avg Time={AverageProcessingTime.TotalMilliseconds:F2}ms";
        }
    }
}