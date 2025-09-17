namespace TradingEngine.Domain.ValueObjects
{
    /// <summary>
    /// Represents a high-precision timestamp for trading events
    /// Includes support for microsecond precision
    /// </summary>
    public readonly struct Timestamp : IEquatable<Timestamp>, IComparable<Timestamp>
    {
        public DateTime Value { get; }
        public long Ticks { get; }
        public long UnixMilliseconds { get; }

        public Timestamp(DateTime value)
        {
            Value = value.ToUniversalTime();
            Ticks = Value.Ticks;
            UnixMilliseconds = ((DateTimeOffset)Value).ToUnixTimeMilliseconds();
        }

        public static Timestamp Now => new(DateTime.UtcNow);

        public static Timestamp Create(DateTime value) => new(value);

        public static Timestamp FromUnixMilliseconds(long milliseconds)
        {
            var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
            return new Timestamp(dateTime);
        }

        public static Timestamp FromTicks(long ticks)
        {
            var dateTime = new DateTime(ticks, DateTimeKind.Utc);
            return new Timestamp(dateTime);
        }

        public TimeSpan ElapsedSince() => DateTime.UtcNow - Value;

        public TimeSpan TimeSince(Timestamp other) => Value - other.Value;

        public Timestamp AddMilliseconds(double milliseconds) => new(Value.AddMilliseconds(milliseconds));

        public Timestamp AddMicroseconds(long microseconds) => new(Value.AddTicks(microseconds * 10));

        public bool IsInFuture => Value > DateTime.UtcNow;

        public bool IsInPast => Value < DateTime.UtcNow;

        public bool Equals(Timestamp other) => Ticks == other.Ticks;

        public override bool Equals(object? obj) => obj is Timestamp other && Equals(other);

        public override int GetHashCode() => Ticks.GetHashCode();

        public int CompareTo(Timestamp other) => Ticks.CompareTo(other.Ticks);

        public override string ToString() => Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff");

        public string ToIsoString() => Value.ToString("O");

        // Operators
        public static bool operator ==(Timestamp left, Timestamp right) => left.Equals(right);
        public static bool operator !=(Timestamp left, Timestamp right) => !left.Equals(right);
        public static bool operator <(Timestamp left, Timestamp right) => left.Ticks < right.Ticks;
        public static bool operator >(Timestamp left, Timestamp right) => left.Ticks > right.Ticks;
        public static bool operator <=(Timestamp left, Timestamp right) => left.Ticks <= right.Ticks;
        public static bool operator >=(Timestamp left, Timestamp right) => left.Ticks >= right.Ticks;

        public static TimeSpan operator -(Timestamp left, Timestamp right) => left.Value - right.Value;

        public static implicit operator DateTime(Timestamp timestamp) => timestamp.Value;
        public static explicit operator Timestamp(DateTime dateTime) => new(dateTime);
    }
}