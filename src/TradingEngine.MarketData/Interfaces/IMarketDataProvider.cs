using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.MarketData.Interfaces
{
    /// <summary>
    /// Interface for market data providers
    /// </summary>
    public interface IMarketDataProvider
    {
        /// <summary>
        /// Subscribe to market data for specific symbols
        /// </summary>
        Task SubscribeAsync(IEnumerable<Symbol> symbols, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unsubscribe from specific symbols
        /// </summary>
        Task UnsubscribeAsync(IEnumerable<Symbol> symbols, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get streaming market data
        /// </summary>
        IAsyncEnumerable<Tick> StreamTicksAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Get the latest tick for a symbol
        /// </summary>
        Task<Tick?> GetLatestTickAsync(Symbol symbol, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get snapshot of all subscribed symbols
        /// </summary>
        Task<IDictionary<Symbol, Tick>> GetMarketSnapshotAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Check if provider is connected
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connect to market data source
        /// </summary>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Disconnect from market data source
        /// </summary>
        Task DisconnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Event raised when connection status changes
        /// </summary>
        event EventHandler<bool>? ConnectionStatusChanged;
    }
}