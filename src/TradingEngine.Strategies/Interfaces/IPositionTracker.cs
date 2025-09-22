using TradingEngine.Domain.Entities;
using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Strategies.Interfaces
{
    /// <summary>
    /// Interface for tracking position information
    /// </summary>
    public interface IPositionTracker
    {
        void UpdatePosition(Position position);
        Position? GetPosition(Symbol symbol);
        IReadOnlyDictionary<Symbol, Position> GetAllPositions();
        decimal CalculateRealizedPnL();
        decimal CalculateUnrealizedPnL();
        decimal CalculateWinRate();
    }
}