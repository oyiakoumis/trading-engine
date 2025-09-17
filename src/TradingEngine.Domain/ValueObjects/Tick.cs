namespace TradingEngine.Domain.ValueObjects
{
    /// <summary>
    /// Represents a market data tick with bid/ask prices and volumes
    /// Immutable value object for real-time market data
    /// </summary>
    public readonly struct Tick : IEquatable<Tick>
    {
        public Symbol Symbol { get; }
        public Price Bid { get; }
        public Price Ask { get; }
        public Quantity BidSize { get; }
        public Quantity AskSize { get; }
        public Timestamp Timestamp { get; }
        public Price Last { get; }
        public Quantity LastSize { get; }
        public long SequenceNumber { get; }

        public Tick(
            Symbol symbol,
            Price bid,
            Price ask,
            Quantity bidSize,
            Quantity askSize,
            Timestamp timestamp,
            Price last,
            Quantity lastSize,
            long sequenceNumber = 0)
        {
            if (bid > ask)
                throw new ArgumentException($"Bid price {bid} cannot be greater than ask price {ask}");

            Symbol = symbol;
            Bid = bid;
            Ask = ask;
            BidSize = bidSize;
            AskSize = askSize;
            Timestamp = timestamp;
            Last = last;
            LastSize = lastSize;
            SequenceNumber = sequenceNumber;
        }

        /// <summary>
        /// Calculate the spread between bid and ask
        /// </summary>
        public Price Spread => Ask - Bid;

        /// <summary>
        /// Calculate the mid-price
        /// </summary>
        public Price MidPrice => new((Bid.Value + Ask.Value) / 2, Math.Max(Bid.Precision, Ask.Precision));

        /// <summary>
        /// Check if this is a valid quote (non-zero bid/ask)
        /// </summary>
        public bool IsValid => Bid.Value > 0 && Ask.Value > 0 && BidSize.Value > 0 && AskSize.Value > 0;

        /// <summary>
        /// Calculate the spread in basis points
        /// </summary>
        public decimal SpreadBps => MidPrice.Value > 0 ? (Spread.Value / MidPrice.Value) * 10000 : 0;

        /// <summary>
        /// Get the age of this tick
        /// </summary>
        public TimeSpan Age => Timestamp.ElapsedSince();

        /// <summary>
        /// Check if tick is stale (older than specified milliseconds)
        /// </summary>
        public bool IsStale(int maxAgeMilliseconds) => Age.TotalMilliseconds > maxAgeMilliseconds;

        /// <summary>
        /// Create a new tick with updated prices
        /// </summary>
        public Tick UpdatePrices(Price? bid = null, Price? ask = null, Price? last = null)
        {
            return new Tick(
                Symbol,
                bid ?? Bid,
                ask ?? Ask,
                BidSize,
                AskSize,
                Timestamp.Now,
                last ?? Last,
                LastSize,
                SequenceNumber + 1
            );
        }

        /// <summary>
        /// Create a new tick with updated sizes
        /// </summary>
        public Tick UpdateSizes(Quantity? bidSize = null, Quantity? askSize = null, Quantity? lastSize = null)
        {
            return new Tick(
                Symbol,
                Bid,
                Ask,
                bidSize ?? BidSize,
                askSize ?? AskSize,
                Timestamp.Now,
                Last,
                lastSize ?? LastSize,
                SequenceNumber + 1
            );
        }

        public bool Equals(Tick other)
        {
            return Symbol == other.Symbol &&
                   Bid == other.Bid &&
                   Ask == other.Ask &&
                   BidSize == other.BidSize &&
                   AskSize == other.AskSize &&
                   Timestamp == other.Timestamp &&
                   Last == other.Last &&
                   LastSize == other.LastSize &&
                   SequenceNumber == other.SequenceNumber;
        }

        public override bool Equals(object? obj) => obj is Tick other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Symbol);
            hash.Add(Bid);
            hash.Add(Ask);
            hash.Add(Timestamp);
            hash.Add(SequenceNumber);
            return hash.ToHashCode();
        }

        public override string ToString()
        {
            return $"{Symbol} | Bid: {Bid} x {BidSize} | Ask: {Ask} x {AskSize} | " +
                   $"Last: {Last} x {LastSize} | Spread: {Spread} ({SpreadBps:F2} bps) | " +
                   $"Time: {Timestamp}";
        }

        public static bool operator ==(Tick left, Tick right) => left.Equals(right);
        public static bool operator !=(Tick left, Tick right) => !left.Equals(right);
    }
}