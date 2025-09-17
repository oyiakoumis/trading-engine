namespace TradingEngine.Domain.ValueObjects
{
    /// <summary>
    /// Represents a trading symbol (e.g., AAPL, MSFT, EURUSD)
    /// Immutable value object with validation
    /// </summary>
    public readonly struct Symbol : IEquatable<Symbol>, IComparable<Symbol>
    {
        private readonly string _value;

        public Symbol(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Symbol cannot be null or empty", nameof(value));

            if (value.Length > 10)
                throw new ArgumentException("Symbol cannot exceed 10 characters", nameof(value));

            _value = value.ToUpperInvariant();
        }

        public string Value => _value ?? string.Empty;

        public static Symbol Create(string value) => new(value);

        public bool Equals(Symbol other) => string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is Symbol other && Equals(other);

        public override int GetHashCode() => _value?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;

        public int CompareTo(Symbol other) => string.Compare(_value, other._value, StringComparison.OrdinalIgnoreCase);

        public override string ToString() => _value ?? string.Empty;

        public static bool operator ==(Symbol left, Symbol right) => left.Equals(right);

        public static bool operator !=(Symbol left, Symbol right) => !left.Equals(right);

        public static implicit operator string(Symbol symbol) => symbol.Value;

        public static explicit operator Symbol(string value) => new(value);
    }
}