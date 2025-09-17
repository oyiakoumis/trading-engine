using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;

namespace TradingEngine.Infrastructure.EventBus
{
    /// <summary>
    /// High-performance in-memory event bus implementation
    /// Uses channels for async processing and concurrent collections for subscriptions
    /// </summary>
    public class InMemoryEventBus : IEventBus, IDisposable
    {
        private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions;
        private readonly List<Subscription> _globalSubscriptions;
        private readonly Channel<EventEnvelope> _eventChannel;
        private readonly ILogger<InMemoryEventBus>? _logger;
        private readonly SemaphoreSlim _subscriptionSemaphore;
        private readonly CancellationTokenSource _shutdownCts;
        private readonly Task _processingTask;
        private readonly EventBusStatistics _statistics;
        private readonly object _statsLock = new();
        private bool _disposed;

        // Configuration
        public int MaxQueueSize { get; set; } = 10000;
        public int MaxConcurrency { get; set; } = 10;
        public TimeSpan EventTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public InMemoryEventBus(ILogger<InMemoryEventBus>? logger = null)
        {
            _logger = logger;
            _subscriptions = new ConcurrentDictionary<Type, List<Subscription>>();
            _globalSubscriptions = new List<Subscription>();
            _subscriptionSemaphore = new SemaphoreSlim(1, 1);
            _statistics = new EventBusStatistics();
            _shutdownCts = new CancellationTokenSource();

            var channelOptions = new BoundedChannelOptions(MaxQueueSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            };

            _eventChannel = Channel.CreateBounded<EventEnvelope>(channelOptions);
            _processingTask = ProcessEventsAsync(_shutdownCts.Token);
        }

        public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IEvent
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InMemoryEventBus));

            var envelope = new EventEnvelope(@event, typeof(TEvent));

            await _eventChannel.Writer.WriteAsync(envelope, cancellationToken);

            lock (_statsLock)
            {
                _statistics.TotalEventsPublished++;
                _statistics.LastEventTime = DateTime.UtcNow;
                _statistics.QueuedEvents = _eventChannel.Reader.Count;
            }

            _logger?.LogDebug("Published event {EventType} with ID {EventId}",
                @event.EventType, @event.EventId);
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler)
            where TEvent : IEvent
        {
            return Subscribe<TEvent>(handler, null);
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler, Func<TEvent, bool>? filter)
            where TEvent : IEvent
        {
            var subscription = new TypedSubscription<TEvent>(handler, filter);

            _subscriptionSemaphore.Wait();
            try
            {
                var eventType = typeof(TEvent);
                if (!_subscriptions.TryGetValue(eventType, out var list))
                {
                    list = new List<Subscription>();
                    _subscriptions[eventType] = list;
                }
                list.Add(subscription);

                lock (_statsLock)
                {
                    _statistics.ActiveSubscriptions++;
                }

                _logger?.LogDebug("Added subscription for event type {EventType}", eventType.Name);
            }
            finally
            {
                _subscriptionSemaphore.Release();
            }

            return new SubscriptionToken(() => Unsubscribe(subscription, typeof(TEvent)));
        }

        public IDisposable SubscribeAll(Func<IEvent, Task> handler)
        {
            var subscription = new GlobalSubscription(handler);

            _subscriptionSemaphore.Wait();
            try
            {
                _globalSubscriptions.Add(subscription);

                lock (_statsLock)
                {
                    _statistics.ActiveSubscriptions++;
                }

                _logger?.LogDebug("Added global subscription");
            }
            finally
            {
                _subscriptionSemaphore.Release();
            }

            return new SubscriptionToken(() => UnsubscribeGlobal(subscription));
        }

        public EventBusStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new EventBusStatistics
                {
                    TotalEventsPublished = _statistics.TotalEventsPublished,
                    TotalEventsProcessed = _statistics.TotalEventsProcessed,
                    TotalEventsFailed = _statistics.TotalEventsFailed,
                    ActiveSubscriptions = _statistics.ActiveSubscriptions,
                    QueuedEvents = _eventChannel.Reader.Count,
                    AverageProcessingTime = _statistics.AverageProcessingTime,
                    LastEventTime = _statistics.LastEventTime
                };
            }
        }

        private async Task ProcessEventsAsync(CancellationToken cancellationToken)
        {
            var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

            await foreach (var envelope in _eventChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await semaphore.WaitAsync(cancellationToken);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessEventAsync(envelope);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);
            }
        }

        private async Task ProcessEventAsync(EventEnvelope envelope)
        {
            var stopwatch = Stopwatch.StartNew();
            var handlers = new List<Func<Task>>();

            // Get type-specific handlers
            if (_subscriptions.TryGetValue(envelope.EventType, out var subscriptions))
            {
                await _subscriptionSemaphore.WaitAsync();
                try
                {
                    foreach (var subscription in subscriptions.ToList())
                    {
                        if (subscription.CanHandle(envelope.Event))
                        {
                            handlers.Add(() => subscription.HandleAsync(envelope.Event));
                        }
                    }
                }
                finally
                {
                    _subscriptionSemaphore.Release();
                }
            }

            // Get global handlers
            await _subscriptionSemaphore.WaitAsync();
            try
            {
                foreach (var subscription in _globalSubscriptions.ToList())
                {
                    handlers.Add(() => subscription.HandleAsync(envelope.Event));
                }
            }
            finally
            {
                _subscriptionSemaphore.Release();
            }

            // Execute handlers with timeout
            if (handlers.Any())
            {
                using var cts = new CancellationTokenSource(EventTimeout);
                var tasks = handlers.Select(h => ExecuteHandlerAsync(h, cts.Token));

                try
                {
                    await Task.WhenAll(tasks);

                    lock (_statsLock)
                    {
                        _statistics.TotalEventsProcessed++;
                        UpdateAverageProcessingTime(stopwatch.Elapsed);
                    }
                }
                catch (Exception ex)
                {
                    lock (_statsLock)
                    {
                        _statistics.TotalEventsFailed++;
                    }

                    _logger?.LogError(ex, "Error processing event {EventType}", envelope.Event.EventType);
                }
            }

            lock (_statsLock)
            {
                _statistics.QueuedEvents = _eventChannel.Reader.Count;
            }
        }

        private async Task ExecuteHandlerAsync(Func<Task> handler, CancellationToken cancellationToken)
        {
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("Event handler timed out");
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in event handler");
                throw;
            }
        }

        private void UpdateAverageProcessingTime(TimeSpan elapsed)
        {
            if (_statistics.AverageProcessingTime == TimeSpan.Zero)
            {
                _statistics.AverageProcessingTime = elapsed;
            }
            else
            {
                // Exponential moving average
                var alpha = 0.1;
                _statistics.AverageProcessingTime = TimeSpan.FromMilliseconds(
                    _statistics.AverageProcessingTime.TotalMilliseconds * (1 - alpha) +
                    elapsed.TotalMilliseconds * alpha
                );
            }
        }

        private void Unsubscribe(Subscription subscription, Type eventType)
        {
            _subscriptionSemaphore.Wait();
            try
            {
                if (_subscriptions.TryGetValue(eventType, out var list))
                {
                    list.Remove(subscription);
                    if (!list.Any())
                    {
                        _subscriptions.TryRemove(eventType, out _);
                    }
                }

                lock (_statsLock)
                {
                    _statistics.ActiveSubscriptions--;
                }
            }
            finally
            {
                _subscriptionSemaphore.Release();
            }
        }

        private void UnsubscribeGlobal(Subscription subscription)
        {
            _subscriptionSemaphore.Wait();
            try
            {
                _globalSubscriptions.Remove(subscription);

                lock (_statsLock)
                {
                    _statistics.ActiveSubscriptions--;
                }
            }
            finally
            {
                _subscriptionSemaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            _shutdownCts.Cancel();
            _eventChannel.Writer.TryComplete();

            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during event bus shutdown");
            }

            _shutdownCts.Dispose();
            _subscriptionSemaphore.Dispose();
        }

        /// <summary>
        /// Event envelope for internal processing
        /// </summary>
        private class EventEnvelope
        {
            public IEvent Event { get; }
            public Type EventType { get; }

            public EventEnvelope(IEvent @event, Type eventType)
            {
                Event = @event;
                EventType = eventType;
            }
        }

        /// <summary>
        /// Base subscription class
        /// </summary>
        private abstract class Subscription
        {
            public abstract Task HandleAsync(IEvent @event);
            public abstract bool CanHandle(IEvent @event);
        }

        /// <summary>
        /// Typed subscription for specific event types
        /// </summary>
        private class TypedSubscription<TEvent> : Subscription where TEvent : IEvent
        {
            private readonly Func<TEvent, Task> _handler;
            private readonly Func<TEvent, bool>? _filter;

            public TypedSubscription(Func<TEvent, Task> handler, Func<TEvent, bool>? filter)
            {
                _handler = handler;
                _filter = filter;
            }

            public override async Task HandleAsync(IEvent @event)
            {
                if (@event is TEvent typedEvent)
                {
                    await _handler(typedEvent);
                }
            }

            public override bool CanHandle(IEvent @event)
            {
                if (@event is TEvent typedEvent)
                {
                    return _filter?.Invoke(typedEvent) ?? true;
                }
                return false;
            }
        }

        /// <summary>
        /// Global subscription for all events
        /// </summary>
        private class GlobalSubscription : Subscription
        {
            private readonly Func<IEvent, Task> _handler;

            public GlobalSubscription(Func<IEvent, Task> handler)
            {
                _handler = handler;
            }

            public override Task HandleAsync(IEvent @event)
            {
                return _handler(@event);
            }

            public override bool CanHandle(IEvent @event)
            {
                return true;
            }
        }

        /// <summary>
        /// Subscription token for unsubscribing
        /// </summary>
        private class SubscriptionToken : IDisposable
        {
            private readonly Action _unsubscribe;
            private bool _disposed;

            public SubscriptionToken(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _unsubscribe();
                }
            }
        }
    }
}