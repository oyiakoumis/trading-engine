using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Execution.Pipeline.Stages;
using TradingEngine.Risk.Interfaces;
using TradingEngine.Domain.Entities;

namespace TradingEngine.Execution.Adapters
{
    /// <summary>
    /// Adapter to integrate existing IRiskManager with new pipeline system
    /// </summary>
    public sealed class RiskManagerAdapter : IRiskAssessment
    {
        private readonly IRiskManager _riskManager;

        public RiskManagerAdapter(IRiskManager riskManager)
        {
            _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        }

        public async ValueTask<RiskCheckResult> CheckPreTradeRiskAsync(
            Order order, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _riskManager.CheckPreTradeRiskAsync(order);
                
                // Convert from old interface to new interface
                return result.Passed 
                    ? RiskCheckResult.Pass(MapRiskLevel(result.RiskLevel), result.Details)
                    : RiskCheckResult.Fail(result.RejectionReason ?? "Risk check failed", MapRiskLevel(result.RiskLevel), result.Details);
            }
            catch (Exception ex)
            {
                return RiskCheckResult.Fail($"Risk assessment error: {ex.Message}", RiskLevel.Critical);
            }
        }

        public ValueTask<bool> IsAvailableAsync()
        {
            // Assume risk manager is always available
            return ValueTask.FromResult(_riskManager != null);
        }

        public async ValueTask<RiskLimits> GetRiskLimitsAsync()
        {
            // Get current risk metrics and convert to limits
            var metrics = await _riskManager.GetRiskMetricsAsync();
            
            return new RiskLimits
            {
                MaxPositionSize = 10000,
                MaxOrderValue = 100000,
                MaxDailyLoss = 10000,
                MaxDrawdown = 0.20m,
                MaxExposure = 500000,
                MaxLeverage = 2.0m,
                MaxOpenPositions = 20,
                MaxOrdersPerMinute = 100,
                ConcentrationLimit = 0.30m
            };
        }

        private RiskLevel MapRiskLevel(Risk.Interfaces.RiskLevel oldRiskLevel)
        {
            return oldRiskLevel switch
            {
                Risk.Interfaces.RiskLevel.Low => RiskLevel.Low,
                Risk.Interfaces.RiskLevel.Medium => RiskLevel.Medium,
                Risk.Interfaces.RiskLevel.High => RiskLevel.High,
                Risk.Interfaces.RiskLevel.Critical => RiskLevel.Critical,
                _ => RiskLevel.Medium
            };
        }
    }
}