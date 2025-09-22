namespace TradingEngine.Strategies.Interfaces
{
    /// <summary>
    /// Interface for managing available capital
    /// </summary>
    public interface ICapitalManager
    {
        decimal AvailableCapital { get; }
        void UpdateCapital(decimal capital);
        decimal CalculateDrawdown(decimal totalPnL);
        bool HasSufficientCapital(decimal requiredAmount);
    }
}