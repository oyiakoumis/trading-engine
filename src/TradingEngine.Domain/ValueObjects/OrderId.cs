namespace TradingEngine.Domain.ValueObjects
{
    /// <summary>
    /// Represents a unique order identifier
    /// Immutable value object for order tracking
    /// </summary>
    public readonly struct OrderId : IEquatable<OrderId>, IComparable<OrderId>
    {
        private readonly Guid _value;

        public OrderId(Guid value)
        {
            if (value == Guid.Empty)
                throw new ArgumentException("OrderId cannot be empty", nameof(value));

            _value = value;
        }

        public OrderId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("OrderId cannot be null or empty", nameof(value));

            if (!Guid.TryParse(value, out var guid))
                throw new ArgumentException("OrderId must be a valid GUID", nameof(value));

            _value = guid;
        }

        public string Value => _value.ToString();

        public static OrderId NewId() => new(Guid.NewGuid());

        public static OrderId Create(Guid value) => new(value);

        public static OrderId Create(string value) => new(value);

        public static OrderId Parse(string value) => new(value);

        public static bool TryParse(string value, out OrderId orderId)
        {
            if (Guid.TryParse(value, out var guid) && guid != Guid.Empty)
            {
                orderId = new OrderId(guid);
                return true;
            }

            orderId = default;
            return false;
        }

        public bool Equals(OrderId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is OrderId other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public int CompareTo(OrderId other) => _value.CompareTo(other._value);

        public override string ToString() => _value.ToString("N"); // No hyphens for compact format

        public string ToShortString() => _value.ToString("N")[..8]; // First 8 characters

        // Operators
        public static bool operator ==(OrderId left, OrderId right) => left.Equals(right);
        public static bool operator !=(OrderId left, OrderId right) => !left.Equals(right);

        public static implicit operator string(OrderId orderId) => orderId.Value;
        public static explicit operator OrderId(string value) => new(value);
        public static explicit operator OrderId(Guid value) => new(value);
    }
}