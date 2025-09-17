namespace TradingEngine.Domain.ValueObjects
{
    /// <summary>
    /// Represents a trading quantity/volume
    /// Immutable value object for order and position sizes
    /// </summary>
    public readonly struct Quantity : IEquatable<Quantity>, IComparable<Quantity>
    {
        private const decimal MinValue = 0;
        private const decimal MaxValue = 1_000_000_000;

        public decimal Value { get; }

        public Quantity(decimal value)
        {
            if (value < MinValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"Quantity cannot be negative");

            if (value > MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"Quantity cannot exceed {MaxValue}");

            Value = Math.Round(value, 0); // Round to whole numbers for simplicity
        }

        public static Quantity Create(decimal value) => new(value);

        public static Quantity Zero => new(0);

        public Quantity Add(Quantity other) => new(Value + other.Value);

        public Quantity Subtract(Quantity other)
        {
            if (other.Value > Value)
                throw new InvalidOperationException($"Cannot subtract {other.Value} from {Value}");

            return new Quantity(Value - other.Value);
        }

        public Quantity Multiply(decimal multiplier)
        {
            if (multiplier < 0)
                throw new ArgumentException("Multiplier cannot be negative", nameof(multiplier));

            return new Quantity(Value * multiplier);
        }

        public (Quantity quotient, Quantity remainder) Divide(Quantity divisor)
        {
            if (divisor.Value == 0)
                throw new DivideByZeroException("Cannot divide by zero quantity");

            var quotient = Math.Floor(Value / divisor.Value);
            var remainder = Value % divisor.Value;

            return (new Quantity(quotient), new Quantity(remainder));
        }

        public bool IsZero => Value == 0;

        public bool Equals(Quantity other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is Quantity other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public int CompareTo(Quantity other) => Value.CompareTo(other.Value);

        public override string ToString() => Value.ToString("N0");

        // Operators
        public static bool operator ==(Quantity left, Quantity right) => left.Equals(right);
        public static bool operator !=(Quantity left, Quantity right) => !left.Equals(right);
        public static bool operator <(Quantity left, Quantity right) => left.Value < right.Value;
        public static bool operator >(Quantity left, Quantity right) => left.Value > right.Value;
        public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;
        public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;

        public static Quantity operator +(Quantity left, Quantity right) => left.Add(right);
        public static Quantity operator -(Quantity left, Quantity right) => left.Subtract(right);
        public static Quantity operator *(Quantity quantity, decimal multiplier) => quantity.Multiply(multiplier);
        public static Quantity operator *(decimal multiplier, Quantity quantity) => quantity.Multiply(multiplier);

        public static implicit operator decimal(Quantity quantity) => quantity.Value;
        public static explicit operator Quantity(decimal value) => new(value);
        public static explicit operator Quantity(int value) => new(value);
    }
}