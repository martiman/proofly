using System.Text.Json;
using Proofly.Contracts.Common;

namespace Proofly.Api.Tests.Platform;

/// <summary>The one money-scale rule of the contract: exact value, at least 2 fraction digits, trailing zeros beyond the second removed, never rounded.</summary>
public class DecimalStringTests
{
    [Theory]
    [InlineData("0", "0.00")]
    [InlineData("0.00", "0.00")]
    [InlineData("0.000000", "0.00")]
    [InlineData("1081977.250000", "1081977.25")]
    [InlineData("1081977.25", "1081977.25")]
    [InlineData("-25.500000", "-25.50")]
    [InlineData("0.5", "0.50")]
    [InlineData("1.234500", "1.2345")]
    [InlineData("20.123456", "20.123456")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("100", "100.00")]
    [InlineData("-0.00", "0.00")]
    [InlineData("3000.000000", "3000.00")]
    public void Normalizes_without_rounding(string input, string expected)
    {
        Assert.Equal(expected, DecimalString.Normalize(input));
        Assert.Equal(expected, DecimalString.Normalize(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)); // same value
    }

    [Fact]
    public void Sub_precision_digits_are_kept_exactly()
    {
        Assert.Equal("0.0000000000000000000000000001", DecimalString.Normalize(0.0000000000000000000000000001m));
        Assert.Equal("79228162514264337593543950335.00", DecimalString.Normalize(decimal.MaxValue));
    }

    [Fact]
    public void Money_dto_serialises_the_normalised_amount_whatever_the_caller_passed()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new MoneyDto("1081977.250000", "GBP")));
        Assert.Equal("1081977.25", doc.RootElement.GetProperty("Amount").GetString());
        Assert.Equal("GBP", doc.RootElement.GetProperty("Currency").GetString());
        Assert.Equal("0.00", new MoneyDto("0.000000", "EUR").Amount);
        Assert.Equal("5.00", (new MoneyDto("5", "EUR") with { Currency = "GBP" }).Amount);
        Assert.Equal("not-a-number", new MoneyDto("not-a-number", "EUR").Amount); // never repaired: the contract validator rejects it
    }
}
