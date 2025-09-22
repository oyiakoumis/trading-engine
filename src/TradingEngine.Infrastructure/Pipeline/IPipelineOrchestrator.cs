using TradingEngine.Domain.ValueObjects;

namespace TradingEngine.Infrastructure.Pipeline
{
    /// <summary>
    /// Interface for pipeline orchestration
    /// </summary>
    public interface IPipelineOrchestrator
    {
        Task StartAsync(IEnumerable<Symbol> symbols);
        Task StopAsync();
        bool IsRunning { get; }
        PipelineStatistics GetStatistics();
        event EventHandler<PipelineEventArgs>? PipelineEvent;
    }
}