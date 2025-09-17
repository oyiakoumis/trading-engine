using FluentAssertions;
using TradingEngine.Domain.ValueObjects;
using Xunit;

namespace TradingEngine.UnitTests.Domain.ValueObjects
{
    public class PriceTests
    {
        [Fact]
        public void Constructor_WithValidPrice_ShouldCreate()
        {
            // Arrange & Act
            var price = new Price(100.50m);

            // Assert
            price.Value.Should().Be(100.50m);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-100.50)]
        public void Constructor_WithNegativePrice_ShouldThrow(decimal value)
        {
            // Act & Assert
            var act = () => new Price(value);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Price cannot be negative*");
        }

        [Fact]
        public void Constructor_FromDecimal_ShouldWork()
        {
            // Arrange & Act
            var price = new Price(150.75m);

            // Assert
            price.Value.Should().Be(150.75m);
        }

        [Fact]
        public void ImplicitOperator_ToDecimal_ShouldConvert()
        {
            // Arrange
            var price = new Price(200.25m);

            // Act
            decimal value = price;

            // Assert
            value.Should().Be(200.25m);
        }

        [Fact]
        public void Addition_ShouldReturnCorrectResult()
        {
            // Arrange
            var price1 = new Price(100m);
            var price2 = new Price(50m);

            // Act
            var result = price1 + price2;

            // Assert
            result.Value.Should().Be(150m);
        }

        [Fact]
        public void Subtraction_ShouldReturnCorrectResult()
        {
            // Arrange
            var price1 = new Price(100m);
            var price2 = new Price(30m);

            // Act
            var result = price1 - price2;

            // Assert
            result.Value.Should().Be(70m);
        }

        [Fact]
        public void Multiplication_WithDecimal_ShouldReturnCorrectResult()
        {
            // Arrange
            var price = new Price(100m);

            // Act
            var result = price * 1.5m;

            // Assert
            result.Value.Should().Be(150m);
        }

        [Fact]
        public void Division_WithDecimal_ShouldReturnCorrectResult()
        {
            // Arrange
            var price = new Price(100m);

            // Act
            var result = price / 2m;

            // Assert
            result.Should().Be(50m);
        }

        [Fact]
        public void CompareTo_ShouldCompareCorrectly()
        {
            // Arrange
            var price1 = new Price(100m);
            var price2 = new Price(150m);
            var price3 = new Price(100m);

            // Act & Assert
            (price1 < price2).Should().BeTrue();
            (price2 > price1).Should().BeTrue();
            (price1 <= price3).Should().BeTrue();
            (price1 >= price3).Should().BeTrue();
            (price1 == price3).Should().BeTrue();
            (price1 != price2).Should().BeTrue();
        }

        [Fact]
        public void Equals_WithSameValue_ShouldReturnTrue()
        {
            // Arrange
            var price1 = new Price(100.50m);
            var price2 = new Price(100.50m);

            // Act & Assert
            price1.Should().Be(price2);
            price1.Equals(price2).Should().BeTrue();
        }

        [Fact]
        public void GetHashCode_ForEqualPrices_ShouldBeSame()
        {
            // Arrange
            var price1 = new Price(100.50m);
            var price2 = new Price(100.50m);

            // Act & Assert
            price1.GetHashCode().Should().Be(price2.GetHashCode());
        }

        [Fact]
        public void ToString_WithPrecision_ShouldFormatCorrectly()
        {
            // Arrange
            var price = new Price(100.5678m);

            // Act
            var result = price.ToString("F2");

            // Assert
            result.Should().Be("100.57");
        }
    }
}