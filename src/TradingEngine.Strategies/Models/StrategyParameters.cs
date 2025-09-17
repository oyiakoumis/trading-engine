namespace TradingEngine.Strategies.Models
{
    /// <summary>
    /// Base class for strategy parameters
    /// </summary>
    public class StrategyParameters
    {
        private readonly Dictionary<string, object> _parameters;

        public StrategyParameters()
        {
            _parameters = new Dictionary<string, object>();
        }

        /// <summary>
        /// Set a parameter value
        /// </summary>
        public void SetParameter<T>(string name, T value) where T : notnull
        {
            _parameters[name] = value;
        }

        /// <summary>
        /// Get a parameter value
        /// </summary>
        public T GetParameter<T>(string name, T defaultValue = default!)
        {
            if (_parameters.TryGetValue(name, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Check if parameter exists
        /// </summary>
        public bool HasParameter(string name) => _parameters.ContainsKey(name);

        /// <summary>
        /// Get all parameter names
        /// </summary>
        public IEnumerable<string> GetParameterNames() => _parameters.Keys;

        /// <summary>
        /// Clear all parameters
        /// </summary>
        public void Clear() => _parameters.Clear();

        /// <summary>
        /// Clone parameters
        /// </summary>
        public StrategyParameters Clone()
        {
            var clone = new StrategyParameters();
            foreach (var kvp in _parameters)
            {
                clone._parameters[kvp.Key] = kvp.Value;
            }
            return clone;
        }
    }

    /// <summary>
    /// Momentum strategy specific parameters
    /// </summary>
    public class MomentumStrategyParameters : StrategyParameters
    {
        public int LookbackPeriod
        {
            get => GetParameter("LookbackPeriod", 20);
            set => SetParameter("LookbackPeriod", value);
        }

        public decimal MomentumThreshold
        {
            get => GetParameter("MomentumThreshold", 2.0m);
            set => SetParameter("MomentumThreshold", value);
        }

        public decimal TakeProfitPercent
        {
            get => GetParameter("TakeProfitPercent", 2.0m);
            set => SetParameter("TakeProfitPercent", value);
        }

        public decimal StopLossPercent
        {
            get => GetParameter("StopLossPercent", 1.0m);
            set => SetParameter("StopLossPercent", value);
        }

        public bool UseTrailingStop
        {
            get => GetParameter("UseTrailingStop", false);
            set => SetParameter("UseTrailingStop", value);
        }

        public decimal PositionSizePercent
        {
            get => GetParameter("PositionSizePercent", 10.0m);
            set => SetParameter("PositionSizePercent", value);
        }

        public double MinConfidence
        {
            get => GetParameter("MinConfidence", 0.6);
            set => SetParameter("MinConfidence", value);
        }

        public MomentumStrategyParameters()
        {
            // Set default values
            LookbackPeriod = 20;
            MomentumThreshold = 2.0m;
            TakeProfitPercent = 2.0m;
            StopLossPercent = 1.0m;
            UseTrailingStop = false;
            PositionSizePercent = 10.0m;
            MinConfidence = 0.6;
        }

        public override string ToString()
        {
            return $"Momentum Parameters: Lookback={LookbackPeriod}, Threshold={MomentumThreshold}%, " +
                   $"TP={TakeProfitPercent}%, SL={StopLossPercent}%, " +
                   $"TrailingStop={UseTrailingStop}, Size={PositionSizePercent}%";
        }
    }
}