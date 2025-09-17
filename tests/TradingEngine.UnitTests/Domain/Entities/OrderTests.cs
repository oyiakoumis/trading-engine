using FluentAssertions;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using Xunit;

namespace TradingEngine.UnitTests.Domain.Entities
{
    public class OrderTests
    {
        [Fact]
        public void Constructor_MarketOrder_ShouldCreateOrderWithCorrectProperties()
        {
            // Arrange
            var symbol = new Symbol("AAPL");
            var side = OrderSide.Buy;
            var type = OrderType.Market;
            var quantity = new Quantity(100);

            // Act
            var order = new Order(symbol, side, type, quantity);

            // Assert
            order.Should().NotBeNull();
            order.Id.Should().NotBeNull();
            order.Id.Value.Should().NotBeEmpty();
            order.Symbol.Should().Be(symbol);
            order.Quantity.Should().Be(quantity);
            order.Side.Should().Be(side);
            order.Type.Should().Be(OrderType.Market);
            order.Status.Should().Be(OrderStatus.Pending);
            order.LimitPrice.Should().BeNull();
            order.FilledQuantity.Should().Be(Quantity.Zero);
            order.CreatedAt.Should().NotBe(default(Timestamp));
        }

        [Fact]
        public void Constructor_LimitOrder_ShouldCreateOrderWithLimitPrice()
        {
            // Arrange
            var symbol = new Symbol("MSFT");
            var side = OrderSide.Sell;
            var type = OrderType.Limit;
            var quantity = new Quantity(50);
            var limitPrice = new Price(300.50m);

            // Act
            var order = new Order(symbol, side, type, quantity, limitPrice);

            // Assert
            order.Should().NotBeNull();
            order.Symbol.Should().Be(symbol);
            order.Quantity.Should().Be(quantity);
            order.Side.Should().Be(side);
            order.Type.Should().Be(OrderType.Limit);
            order.Status.Should().Be(OrderStatus.Pending);
            order.LimitPrice.Should().Be(limitPrice);
        }

        [Fact]
        public void Constructor_ShouldCreateOrderWithPendingStatus()
        {
            // Arrange & Act
            var order = new Order(new Symbol("GOOGL"), OrderSide.Buy, OrderType.Market, new Quantity(10));

            // Assert
            order.Status.Should().Be(OrderStatus.Pending);
        }

        [Fact]
        public void Fill_WithFullQuantity_ShouldMarkAsCompleted()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var fillPrice = new Price(150.50m);

            // Act
            order.Fill(order.Quantity, fillPrice);

            // Assert
            order.Status.Should().Be(OrderStatus.Filled);
            order.FilledQuantity.Should().Be(order.Quantity);
        }

        [Fact]
        public void Fill_WithPartialQuantity_ShouldMarkAsPartiallyFilled()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var partialQuantity = new Quantity(30);
            var fillPrice = new Price(150.50m);

            // Act
            order.Fill(partialQuantity, fillPrice);

            // Assert
            order.Status.Should().Be(OrderStatus.PartiallyFilled);
            order.FilledQuantity.Should().Be(partialQuantity);
        }

        [Fact]
        public void Fill_MultipleFills_ShouldAccumulateQuantity()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var fill1Quantity = new Quantity(30);
            var fill1Price = new Price(150.00m);
            var fill2Quantity = new Quantity(70);
            var fill2Price = new Price(151.00m);

            // Act
            order.Fill(fill1Quantity, fill1Price);
            order.Fill(fill2Quantity, fill2Price);

            // Assert
            order.Status.Should().Be(OrderStatus.Filled);
            order.FilledQuantity.Should().Be(order.Quantity);
        }

        [Fact]
        public void Fill_WhenAlreadyFilled_ShouldThrow()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var fillPrice = new Price(150.50m);
            order.Fill(order.Quantity, fillPrice);

            // Act & Assert
            var act = () => order.Fill(new Quantity(10), fillPrice);
            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Cancel_WhenPending_ShouldMarkAsCancelled()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));

            // Act
            order.Cancel();

            // Assert
            order.Status.Should().Be(OrderStatus.Cancelled);
        }

        [Fact]
        public void Cancel_WhenFilled_ShouldThrow()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            order.Fill(order.Quantity, new Price(150m));

            // Act & Assert
            var act = () => order.Cancel();
            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Reject_WithReason_ShouldMarkAsRejected()
        {
            // Arrange
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var reason = "Insufficient funds";

            // Act
            order.Reject(reason);

            // Assert
            order.Status.Should().Be(OrderStatus.Rejected);
        }

        [Fact]
        public void GetRemainingQuantity_ShouldReturnCorrectValue()
        {
            // Arrange
            var totalQuantity = new Quantity(100);
            var order = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, totalQuantity);
            var fillQuantity = new Quantity(30);

            // Act
            order.Fill(fillQuantity, new Price(150m));
            var remaining = order.Quantity - order.FilledQuantity;

            // Assert
            remaining.Should().Be(new Quantity(70));
        }

        [Fact]
        public void OrderFilled_ShouldReturnCorrectValue()
        {
            // Arrange
            var order1 = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var order2 = new Order(new Symbol("MSFT"), OrderSide.Sell, OrderType.Market, new Quantity(50));

            // Act
            order1.Fill(order1.Quantity, new Price(150m));
            order2.Fill(new Quantity(25), new Price(300m));

            // Assert
            order1.Status.Should().Be(OrderStatus.Filled);
            order2.Status.Should().Be(OrderStatus.PartiallyFilled);
        }

        [Fact]
        public void OrderStatus_IsActive_ShouldReturnCorrectValue()
        {
            // Arrange
            var pendingOrder = new Order(new Symbol("AAPL"), OrderSide.Buy, OrderType.Market, new Quantity(100));
            var partialOrder = new Order(new Symbol("GOOGL"), OrderSide.Buy, OrderType.Market, new Quantity(75));
            var filledOrder = new Order(new Symbol("AMZN"), OrderSide.Sell, OrderType.Market, new Quantity(25));
            var cancelledOrder = new Order(new Symbol("MSFT"), OrderSide.Sell, OrderType.Market, new Quantity(50));

            partialOrder.Fill(new Quantity(50), new Price(2000m));
            filledOrder.Fill(filledOrder.Quantity, new Price(3000m));
            cancelledOrder.Cancel();

            // Act & Assert
            pendingOrder.Status.IsActive().Should().BeTrue();
            partialOrder.Status.IsActive().Should().BeTrue();
            filledOrder.Status.IsActive().Should().BeFalse();
            cancelledOrder.Status.IsActive().Should().BeFalse();
        }
    }
}