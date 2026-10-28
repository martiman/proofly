using Proofly.Domain;

namespace Proofly.Connectors.Tests;

public class ManifestTotalsTests
{
    private static ManifestTotals.Result Read(params (string, string)[] cells) =>
        ManifestTotals.Read(cells.ToDictionary(c => c.Item1, c => c.Item2));

    [Fact]
    public void Reads_any_number_of_currency_columns_including_zero_decimal_currencies()
    {
        var r = Read(("source_name", "x"), ("declared_total_gbp", "121.75"), ("declared_total_eur", "310"), ("declared_total_jpy", "5000"));
        Assert.Empty(r.Errors);
        Assert.Equal([new Money(310m, "EUR"), new Money(121.75m, "GBP"), new Money(5000m, "JPY")], r.Totals);
    }

    [Fact]
    public void Blank_cell_means_not_declared_and_no_total_column_means_empty()
    {
        Assert.Equal([new Money(1m, "GBP")], Read(("declared_total_gbp", "1"), ("declared_total_eur", " ")).Totals);
        Assert.Empty(Read(("delivery_id", "D1")).Totals);
    }

    [Theory]
    [InlineData("declared_total_xyz", "1")]      // not an ISO 4217 code
    [InlineData("declared_total_GBP", "1")]      // column must be lower-case
    [InlineData("declared_total_eu", "1")]
    [InlineData("declared_total_gbp", "abc")]
    [InlineData("declared_total_gbp", "1,5")]
    [InlineData("declared_total_gbp", "1.1234567")]
    public void Invalid_columns_or_values_are_reported_not_repaired(string column, string value)
    {
        var r = Read((column, value));
        Assert.Single(r.Errors);
        Assert.Empty(r.Totals);
    }
}
