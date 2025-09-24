using TradingEngine.Domain.ValueObjects;
using TradingEngine.Strategies.Models;

namespace TradingEngine.Execution.Pipeline.Models
{
    /// <summary>
    /// Event args for order processing completion
    /// </summary>
    public sealed class OrderProcessingCompletedEventArgs : EventArgs
    {
        public Signal Signal { get; }
        public OrderProcessingResult Result { get; }
        public string? CorrelationId { get; }
        public DateTime CompletedAt { get; }

        public OrderProcessingCompletedEventArgs(
            Signal signal,
            OrderProcessingResult result,
            string? correlationId = null)
        {
            Signal = signal ?? throw new ArgumentNullException(nameof(signal));
            Result = result ?? throw new ArgumentNullException(nameof(result));
            CorrelationId = correlationId;
            CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Event args for order processing errors
    /// </summary>
    public sealed class OrderProcessingErrorEventArgs : EventArgs
    {
        public Signal Signal { get; }
        public Exception Exception { get; }
        public string StageName { get; }
        public string? CorrelationId { get; }
        public DateTime ErrorAt { get; }
        public IReadOnlyDictionary<string, object> Context { get; }

        public OrderProcessingErrorEventArgs(
            Signal signal,
            Exception exception,
            string stageName,
            string? correlationId = null,
            IReadOnlyDictionary<string, object>? context = null)
        {
            Signal = signal ?? throw new ArgumentNullException(nameof(signal));
            Exception = exception ?? throw new ArgumentNullException(nameof(exception));
            StageName = stageName ?? throw new ArgumentNullException(nameof(stageName));
            CorrelationId = correlationId;
            Context = context ?? new Dictionary<string, object>();
            ErrorAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Event args for stage completion
    /// </summary>
    public sealed class StageCompletedEventArgs : EventArgs
    {
        public string StageName { get; }
        public StageResult Result { get; }
        public OrderProcessingContext Context { get; }
        public DateTime CompletedAt { get; }

        public StageCompletedEventArgs(
            string stageName,
            StageResult result,
            OrderProcessingContext context)
        {
            StageName = stageName ?? throw new ArgumentNullException(nameof(stageName));
            Result = result ?? throw new ArgumentNullException(nameof(result));
            Context = context ?? throw new ArgumentNullException(nameof(context));
            CompletedAt = DateTime.UtcNow;
        }
    }
}