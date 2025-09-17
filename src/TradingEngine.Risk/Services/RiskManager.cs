using System.Collections.Concurrent;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Risk.Interfaces;

namespace TradingEngine.Risk.Services
{
    /// <summary>
    /// Manages risk limits and performs risk checks
    /// Thread-safe implementation for real-time risk management
    /// </summary>
    public class RiskManager : IRiskManager, IDisposable
    {
        private readonly ConcurrentDictionary<Symbol, Position> _positions;
        private readonly ConcurrentDictionary<Symbol, decimal> _exposures;
        private readonly SemaphoreSlim _riskSemaphore;
        private readonly Timer _riskMonitoringTimer;
        private RiskLimits _riskLimits;
        private RiskMetrics _currentMetrics;
        private readonly object _metricsLock = new();
        private decimal _availableCapital;
        private decimal _dailyPnL;
        private decimal _maxDrawdown;
        private bool _disposed;

        // Statistics
        private int _orderCount;
        private DateTime _lastOrderTime;
        private readonly Queue<DateTime> _recentOrderTimes;

        public event EventHandler<RiskBreachEventArgs>? RiskBreached;

        public RiskManager(decimal initialCapital = 100000m)
        {
            _positions = new ConcurrentDictionary<Symbol, Position>();
            _exposures = new ConcurrentDictionary<Symbol, decimal>();
            _riskSemaphore = new SemaphoreSlim(1, 1);
            _riskLimits = new RiskLimits();
            _currentMetrics = new RiskMetrics();
            _availableCapital = initialCapital;
            _recentOrderTimes = new Queue<DateTime>();

            // Monitor risk every second
            _riskMonitoringTimer = new Timer(
                async _ => await MonitorRiskAsync(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)
            );
        }

        public async Task<RiskCheckResult> CheckPreTradeRiskAsync(Order order)
        {
            await _riskSemaphore.WaitAsync();
            try
            {
                // Check order value limit
                var orderValue = CalculateOrderValue(order);
                if (orderValue > _riskLimits.MaxOrderValue)
                {
                    return RiskCheckResult.Fail(
                        $"Order value {orderValue:C} exceeds limit {_riskLimits.MaxOrderValue:C}",
                        RiskLevel.High
                    );
                }

                // Check position size limit
                if (order.Quantity.Value > _riskLimits.MaxPositionSize)
                {
                    return RiskCheckResult.Fail(
                        $"Order quantity {order.Quantity} exceeds position limit {_riskLimits.MaxPositionSize}",
                        RiskLevel.High
                    );
                }

                // Check daily loss limit
                if (_dailyPnL < -_riskLimits.MaxDailyLoss)
                {
                    return RiskCheckResult.Fail(
                        $"Daily loss {_dailyPnL:C} exceeds limit {_riskLimits.MaxDailyLoss:C}",
                        RiskLevel.Critical
                    );
                }

                // Check exposure limits
                var newExposure = await CalculateNewExposureAsync(order);
                if (newExposure > _riskLimits.MaxExposure)
                {
                    return RiskCheckResult.Fail(
                        $"Total exposure {newExposure:C} would exceed limit {_riskLimits.MaxExposure:C}",
                        RiskLevel.High
                    );
                }

                // Check leverage
                var leverage = newExposure / _availableCapital;
                if (leverage > _riskLimits.MaxLeverage)
                {
                    return RiskCheckResult.Fail(
                        $"Leverage {leverage:F2}x would exceed limit {_riskLimits.MaxLeverage:F2}x",
                        RiskLevel.High
                    );
                }

                // Check concentration limit
                var symbolExposure = await GetExposureAsync(order.Symbol);
                var symbolConcentration = (symbolExposure + orderValue) / _availableCapital;
                if (symbolConcentration > _riskLimits.ConcentrationLimit)
                {
                    return RiskCheckResult.Fail(
                        $"Symbol concentration {symbolConcentration:P} would exceed limit {_riskLimits.ConcentrationLimit:P}",
                        RiskLevel.Medium
                    );
                }

                // Check order rate limit
                if (!CheckOrderRateLimit())
                {
                    return RiskCheckResult.Fail(
                        $"Order rate exceeds {_riskLimits.MaxOrdersPerMinute} per minute",
                        RiskLevel.Medium
                    );
                }

                // Check open positions limit
                if (_positions.Count >= _riskLimits.MaxOpenPositions)
                {
                    return RiskCheckResult.Fail(
                        $"Open positions {_positions.Count} at limit {_riskLimits.MaxOpenPositions}",
                        RiskLevel.Medium
                    );
                }

                // All checks passed
                return RiskCheckResult.Pass(DetermineRiskLevel(leverage, symbolConcentration));
            }
            finally
            {
                _riskSemaphore.Release();
            }
        }

        public async Task<RiskCheckResult> CheckPostTradeRiskAsync(Trade trade)
        {
            await _riskSemaphore.WaitAsync();
            try
            {
                // Update position tracking
                if (!_positions.TryGetValue(trade.Symbol, out var position))
                {
                    position = new Position(trade.Symbol);
                    _positions.TryAdd(trade.Symbol, position);
                }

                position.AddTrade(trade);

                // Update exposures
                var exposure = Math.Abs(position.NetQuantity.Value * trade.ExecutionPrice.Value);
                _exposures.AddOrUpdate(trade.Symbol, exposure, (_, _) => exposure);

                // Check for breaches after trade
                var breaches = await CheckRiskBreachesInternalAsync();
                if (breaches.Any(b => b.Severity >= RiskLevel.High))
                {
                    var criticalBreach = breaches.First(b => b.Severity >= RiskLevel.High);
                    return RiskCheckResult.Fail(
                        $"Post-trade risk breach: {criticalBreach.Description}",
                        criticalBreach.Severity
                    );
                }

                return RiskCheckResult.Pass();
            }
            finally
            {
                _riskSemaphore.Release();
            }
        }

        public void UpdateRiskLimits(RiskLimits limits)
        {
            lock (_metricsLock)
            {
                _riskLimits = limits;
            }
        }

        public async Task<RiskMetrics> GetRiskMetricsAsync()
        {
            await _riskSemaphore.WaitAsync();
            try
            {
                return CalculateRiskMetrics();
            }
            finally
            {
                _riskSemaphore.Release();
            }
        }

        public async Task<decimal> GetExposureAsync(Symbol symbol)
        {
            await Task.CompletedTask;
            return _exposures.TryGetValue(symbol, out var exposure) ? exposure : 0;
        }

        public async Task<decimal> GetTotalExposureAsync()
        {
            await Task.CompletedTask;
            return _exposures.Values.Sum();
        }

        public async Task<IEnumerable<RiskBreach>> CheckRiskBreachesAsync()
        {
            await _riskSemaphore.WaitAsync();
            try
            {
                return await CheckRiskBreachesInternalAsync();
            }
            finally
            {
                _riskSemaphore.Release();
            }
        }

        /// <summary>
        /// Update position for P&L tracking
        /// </summary>
        public void UpdatePosition(Position position)
        {
            _positions.AddOrUpdate(position.Symbol, position, (_, _) => position);

            // Update daily P&L
            _dailyPnL = _positions.Values.Sum(p => p.RealizedPnL);
        }

        /// <summary>
        /// Update market prices for mark-to-market
        /// </summary>
        public void UpdateMarketPrice(Symbol symbol, Price price)
        {
            if (_positions.TryGetValue(symbol, out var position))
            {
                var unrealizedPnL = position.CalculateUnrealizedPnL(price);
                // Update exposure with current market value
                var exposure = Math.Abs(position.NetQuantity.Value * price.Value);
                _exposures.AddOrUpdate(symbol, exposure, (_, _) => exposure);
            }
        }

        private async Task MonitorRiskAsync()
        {
            try
            {
                var breaches = await CheckRiskBreachesInternalAsync();
                foreach (var breach in breaches.Where(b => b.Severity >= RiskLevel.High))
                {
                    RiskBreached?.Invoke(this, new RiskBreachEventArgs(breach, breach.Severity == RiskLevel.Critical));
                }

                // Update metrics
                lock (_metricsLock)
                {
                    _currentMetrics = CalculateRiskMetrics();
                }
            }
            catch (Exception ex)
            {
                // Log error in production
                Console.WriteLine($"Risk monitoring error: {ex.Message}");
            }
        }

        private async Task<List<RiskBreach>> CheckRiskBreachesInternalAsync()
        {
            var breaches = new List<RiskBreach>();
            await Task.CompletedTask;

            var totalExposure = _exposures.Values.Sum();
            var metrics = CalculateRiskMetrics();

            // Check exposure breach
            if (totalExposure > _riskLimits.MaxExposure)
            {
                breaches.Add(new RiskBreach
                {
                    RuleId = "MAX_EXPOSURE",
                    Description = "Total exposure exceeds limit",
                    Severity = RiskLevel.High,
                    CurrentValue = totalExposure,
                    LimitValue = _riskLimits.MaxExposure,
                    DetectedAt = Timestamp.Now,
                    RecommendedAction = "Reduce positions or increase capital"
                });
            }

            // Check leverage breach
            if (metrics.CurrentLeverage > _riskLimits.MaxLeverage)
            {
                breaches.Add(new RiskBreach
                {
                    RuleId = "MAX_LEVERAGE",
                    Description = "Leverage exceeds limit",
                    Severity = RiskLevel.High,
                    CurrentValue = metrics.CurrentLeverage,
                    LimitValue = _riskLimits.MaxLeverage,
                    DetectedAt = Timestamp.Now,
                    RecommendedAction = "Reduce position sizes"
                });
            }

            // Check daily loss breach
            if (_dailyPnL < -_riskLimits.MaxDailyLoss)
            {
                breaches.Add(new RiskBreach
                {
                    RuleId = "MAX_DAILY_LOSS",
                    Description = "Daily loss exceeds limit",
                    Severity = RiskLevel.Critical,
                    CurrentValue = Math.Abs(_dailyPnL),
                    LimitValue = _riskLimits.MaxDailyLoss,
                    DetectedAt = Timestamp.Now,
                    RecommendedAction = "Stop trading for the day"
                });
            }

            // Check drawdown breach
            if (metrics.CurrentDrawdown > _riskLimits.MaxDrawdown)
            {
                breaches.Add(new RiskBreach
                {
                    RuleId = "MAX_DRAWDOWN",
                    Description = "Drawdown exceeds limit",
                    Severity = RiskLevel.Critical,
                    CurrentValue = metrics.CurrentDrawdown,
                    LimitValue = _riskLimits.MaxDrawdown,
                    DetectedAt = Timestamp.Now,
                    RecommendedAction = "Reduce risk or stop trading"
                });
            }

            // Check concentration breaches
            foreach (var kvp in _exposures)
            {
                var concentration = kvp.Value / _availableCapital;
                if (concentration > _riskLimits.ConcentrationLimit)
                {
                    breaches.Add(new RiskBreach
                    {
                        RuleId = $"CONCENTRATION_{kvp.Key}",
                        Description = $"Position concentration in {kvp.Key} exceeds limit",
                        Severity = RiskLevel.Medium,
                        CurrentValue = concentration,
                        LimitValue = _riskLimits.ConcentrationLimit,
                        DetectedAt = Timestamp.Now,
                        RecommendedAction = $"Reduce position in {kvp.Key}"
                    });
                }
            }

            return breaches;
        }

        private decimal CalculateOrderValue(Order order)
        {
            return order.Type switch
            {
                OrderType.Market => order.Quantity.Value * 100, // Estimate for market orders
                OrderType.Limit => order.Quantity.Value * (order.LimitPrice?.Value ?? 100),
                OrderType.Stop => order.Quantity.Value * (order.StopPrice?.Value ?? 100),
                _ => order.Quantity.Value * 100
            };
        }

        private async Task<decimal> CalculateNewExposureAsync(Order order)
        {
            var currentExposure = await GetTotalExposureAsync();
            var orderValue = CalculateOrderValue(order);
            return currentExposure + orderValue;
        }

        private bool CheckOrderRateLimit()
        {
            var now = DateTime.UtcNow;

            // Remove old timestamps
            while (_recentOrderTimes.Count > 0 && (now - _recentOrderTimes.Peek()).TotalMinutes > 1)
            {
                _recentOrderTimes.Dequeue();
            }

            // Check if we're within limit
            if (_recentOrderTimes.Count >= _riskLimits.MaxOrdersPerMinute)
            {
                return false;
            }

            _recentOrderTimes.Enqueue(now);
            return true;
        }

        private RiskLevel DetermineRiskLevel(decimal leverage, decimal concentration)
        {
            if (leverage > _riskLimits.MaxLeverage * 0.9m || concentration > _riskLimits.ConcentrationLimit * 0.9m)
                return RiskLevel.High;
            if (leverage > _riskLimits.MaxLeverage * 0.7m || concentration > _riskLimits.ConcentrationLimit * 0.7m)
                return RiskLevel.Medium;
            return RiskLevel.Low;
        }

        private RiskMetrics CalculateRiskMetrics()
        {
            var totalExposure = _exposures.Values.Sum();
            var netExposure = _positions.Values.Sum(p => p.NetQuantity.Value * p.AverageEntryPrice.Value);
            var grossExposure = _positions.Values.Sum(p => Math.Abs(p.NetQuantity.Value) * p.AverageEntryPrice.Value);

            // Calculate drawdown
            var totalPnL = _positions.Values.Sum(p => p.RealizedPnL);
            if (totalPnL < 0)
            {
                var drawdown = Math.Abs(totalPnL / _availableCapital);
                _maxDrawdown = Math.Max(_maxDrawdown, drawdown);
            }

            return new RiskMetrics
            {
                TotalExposure = totalExposure,
                NetExposure = netExposure,
                GrossExposure = grossExposure,
                CurrentLeverage = _availableCapital > 0 ? totalExposure / _availableCapital : 0,
                DailyPnL = _dailyPnL,
                CurrentDrawdown = Math.Abs(totalPnL / _availableCapital),
                MaxDrawdown = _maxDrawdown,
                OpenPositions = _positions.Count(p => p.Value.IsOpen),
                VaR95 = CalculateVaR(),
                ExposureBySymbol = new Dictionary<Symbol, decimal>(_exposures),
                CalculatedAt = Timestamp.Now
            };
        }

        private decimal CalculateVaR()
        {
            // Simplified VaR calculation (would be more complex in production)
            // Using historical simulation or parametric approach
            var portfolioValue = _exposures.Values.Sum();
            var volatility = 0.02m; // 2% daily volatility assumption
            var confidence = 1.645m; // 95% confidence level
            return portfolioValue * volatility * confidence;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _riskMonitoringTimer?.Dispose();
            _riskSemaphore?.Dispose();
        }
    }
}