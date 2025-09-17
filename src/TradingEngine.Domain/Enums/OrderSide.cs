namespace TradingEngine.Domain.Enums
{
    /// <summary>
    /// Represents the side of an order (Buy or Sell)
    /// </summary>
    public enum OrderSide
    {
        Buy = 1,
        Sell = -1
    }

    public static class OrderSideExtensions
    {
        public static int Sign(this OrderSide side) => (int)side;

        public static OrderSide Opposite(this OrderSide side)
            => side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        public static string ToShortString(this OrderSide side)
            => side == OrderSide.Buy ? "B" : "S";
    }
}