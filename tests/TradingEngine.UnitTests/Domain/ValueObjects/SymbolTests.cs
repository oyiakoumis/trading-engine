using FluentAssertions;
using TradingEngine.Domain.ValueObjects;
using Xunit;

namespace TradingEngine.UnitTests.Domain.ValueObjects
{
    public class SymbolTests
    {
        [Fact]
        public void Constructor_WithValidSymbol_ShouldCreate()
        {
            // Arrange & Act
            var symbol = new Symbol("AAPL");

            // Assert
            symbol.Value.Should().Be("AAPL");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Constructor_WithInvalidSymbol_ShouldThrow(string value)
        {
            // Act & Assert
            var act = () => new Symbol(value);
            act.Should().Throw<ArgumentException>()
                .WithMessage("Symbol cannot be null or empty*");
        }

        [Fact]
        public void Constructor_WithString_ShouldCreateSymbol()
        {
            // Arrange & Act
            var symbol = new Symbol("MSFT");

            // Assert
            symbol.Value.Should().Be("MSFT");
        }

        [Fact]
        public void ImplicitOperator_ToString_ShouldConvert()
        {
            // Arrange
            var symbol = new Symbol("GOOGL");

            // Act
            string value = symbol;

            // Assert
            value.Should().Be("GOOGL");
        }

        [Fact]
        public void Equals_WithSameValue_ShouldReturnTrue()
        {
            // Arrange
            var symbol1 = new Symbol("AAPL");
            var symbol2 = new Symbol("AAPL");

            // Act & Assert
            symbol1.Should().Be(symbol2);
            (symbol1 == symbol2).Should().BeTrue();
            (symbol1 != symbol2).Should().BeFalse();
        }

        [Fact]
        public void Equals_WithDifferentValue_ShouldReturnFalse()
        {
            // Arrange
            var symbol1 = new Symbol("AAPL");
            var symbol2 = new Symbol("MSFT");

            // Act & Assert
            symbol1.Should().NotBe(symbol2);
            (symbol1 == symbol2).Should().BeFalse();
            (symbol1 != symbol2).Should().BeTrue();
        }

        [Fact]
        public void GetHashCode_ForEqualSymbols_ShouldBeSame()
        {
            // Arrange
            var symbol1 = new Symbol("AAPL");
            var symbol2 = new Symbol("AAPL");

            // Act & Assert
            symbol1.GetHashCode().Should().Be(symbol2.GetHashCode());
        }
    }
}