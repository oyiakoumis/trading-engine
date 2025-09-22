using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Exchange;

namespace TradingEngine.Execution.Interfaces
{
    /// <summary>
    /// Interface for exchange implementations
    /// </summary>
    public interface IExchange : IDisposable
    {
        // Configuration properties
        int SimulatedLatencyMs { get; set; }
        decimal SlippagePercent { get; set; }
        decimal PartialFillProbability { get; set; }
        decimal RejectProbability { get; set; }

        // Events
        event EventHandler<ExecutionReport>? ExecutionReported;
        event EventHandler<string>? ExchangeError;

        // Methods
        void Start();
        void Stop();
        Task<bool> SubmitOrderAsync(Order order);
        Task<bool> CancelOrderAsync(OrderId orderId);
        void UpdateMarketPrice(Symbol symbol, Price bidPrice, Price askPrice);
    }
}