namespace TradingEngine.Domain.Enums
{
    /// <summary>
    /// Represents the type of order
    /// </summary>
    public enum OrderType
    {
        Market,
        Limit,
        Stop,
        StopLimit
    }

    public static class OrderTypeExtensions
    {
        public static bool RequiresPrice(this OrderType type)
            => type == OrderType.Limit || type == OrderType.StopLimit;

        public static bool RequiresStopPrice(this OrderType type)
            => type == OrderType.Stop || type == OrderType.StopLimit;

        public static bool IsImmediate(this OrderType type)
            => type == OrderType.Market;

        public static string ToShortString(this OrderType type) => type switch
        {
            OrderType.Market => "MKT",
            OrderType.Limit => "LMT",
            OrderType.Stop => "STP",
            OrderType.StopLimit => "STL",
            _ => type.ToString()
        };
    }
}