using TradingEngine.Strategies.Models;

namespace TradingEngine.Execution.Interfaces
{
    /// <summary>
    /// Interface for order routing from signals to execution venues
    /// </summary>
    public interface IOrderRouter : IDisposable
    {
        /// <summary>
        /// Configuration for pre-trade risk checks
        /// </summary>
        bool EnablePreTradeRiskChecks { get; set; }

        /// <summary>
        /// Maximum order value allowed
        /// </summary>
        decimal MaxOrderValue { get; set; }

        /// <summary>
        /// Maximum orders per minute allowed
        /// </summary>
        int MaxOrdersPerMinute { get; set; }

        /// <summary>
        /// Order timeout configuration
        /// </summary>
        TimeSpan OrderTimeout { get; set; }

        /// <summary>
        /// Start the order router
        /// </summary>
        void Start();

        /// <summary>
        /// Stop the order router
        /// </summary>
        void Stop();

        /// <summary>
        /// Submit a signal for routing to an order
        /// </summary>
        /// <param name="signal">The trading signal to route</param>
        void SubmitSignal(Signal signal);
    }
}