namespace TradingEngine.Domain.ValueObjects
{
    /// <summary>
    /// Represents a price with precision handling
    /// Immutable value object for monetary values
    /// </summary>
    public readonly struct Price : IEquatable<Price>, IComparable<Price>
    {
        private const decimal MinValue = 0.0001m;
        private const decimal MaxValue = 1_000_000m;
        private const int DefaultPrecision = 4;

        public decimal Value { get; }
        public int Precision { get; }

        public Price(decimal value, int precision = DefaultPrecision)
        {
            if (value < MinValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"Price cannot be less than {MinValue}");

            if (value > MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"Price cannot be greater than {MaxValue}");

            if (precision < 0 || precision > 8)
                throw new ArgumentOutOfRangeException(nameof(precision), "Precision must be between 0 and 8");

            Precision = precision;
            Value = Math.Round(value, precision);
        }

        public static Price Create(decimal value, int precision = DefaultPrecision) => new(value, precision);

        public static Price Zero => new(0, DefaultPrecision);

        public Price Add(Price other)
        {
            var precision = Math.Max(Precision, other.Precision);
            return new Price(Value + other.Value, precision);
        }

        public Price Subtract(Price other)
        {
            var precision = Math.Max(Precision, other.Precision);
            return new Price(Value - other.Value, precision);
        }

        public Price Multiply(decimal multiplier)
        {
            return new Price(Value * multiplier, Precision);
        }

        public decimal Spread(Price other) => Math.Abs(Value - other.Value);

        public bool Equals(Price other) => Value == other.Value && Precision == other.Precision;

        public override bool Equals(object? obj) => obj is Price other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Value, Precision);

        public int CompareTo(Price other) => Value.CompareTo(other.Value);

        public override string ToString() => Value.ToString($"F{Precision}");

        public string ToString(string format) => Value.ToString(format);

        // Operators
        public static bool operator ==(Price left, Price right) => left.Equals(right);
        public static bool operator !=(Price left, Price right) => !left.Equals(right);
        public static bool operator <(Price left, Price right) => left.Value < right.Value;
        public static bool operator >(Price left, Price right) => left.Value > right.Value;
        public static bool operator <=(Price left, Price right) => left.Value <= right.Value;
        public static bool operator >=(Price left, Price right) => left.Value >= right.Value;

        public static Price operator +(Price left, Price right) => left.Add(right);
        public static Price operator -(Price left, Price right) => left.Subtract(right);
        public static Price operator *(Price price, decimal multiplier) => price.Multiply(multiplier);
        public static Price operator *(decimal multiplier, Price price) => price.Multiply(multiplier);

        public static implicit operator decimal(Price price) => price.Value;
        public static explicit operator Price(decimal value) => new(value);
    }
}