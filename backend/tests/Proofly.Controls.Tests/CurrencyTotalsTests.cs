using Proofly.Domain;
using Proofly.Controls.Totals;

namespace Proofly.Controls.Tests;

public class CurrencyTotalsTests
{
    [Fact]
    public void Sums_per_currency_in_stable_order()
    {
        var r = CurrencyTotals.Sum([new Money(1.10m, "GBP"), new Money(2m, "EUR"), new Money(0.20m, "GBP")]);
        Assert.Equal([new Money(2m, "EUR"), new Money(1.30m, "GBP")], r);
    }
}
