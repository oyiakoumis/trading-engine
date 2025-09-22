using TradingEngine.Domain.ValueObjects;
using TradingEngine.MarketData.Models;

namespace TradingEngine.MarketData.Interfaces
{
    /// <summary>
    /// Interface for market data processing operations
    /// Provides contract for tick validation, processing, and lifecycle management
    /// </summary>
    public interface IMarketDataProcessor : IAsyncDisposable
    {
        /// <summary>
        /// Start processing market data
        /// </summary>
        void Start();

        /// <summary>
        /// Stop processing market data
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Submit a tick for processing
        /// </summary>
        ValueTask<bool> SubmitTickAsync(Tick tick, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get tick history for a symbol
        /// </summary>
        IReadOnlyList<Tick> GetTickHistory(Symbol symbol);

        /// <summary>
        /// Get statistics for a symbol
        /// </summary>
        TickStatistics? GetStatistics(Symbol symbol);
    }
}