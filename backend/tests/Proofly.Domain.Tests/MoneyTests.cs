using Proofly.Domain.Enums;
using Proofly.Domain;

namespace Proofly.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Addition_is_exact_decimal() =>
        Assert.Equal(0.3m, (new Money(0.1m, "EUR") + new Money(0.2m, "EUR")).Amount);

    [Fact]
    public void Mixed_currencies_are_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new Money(1m, "EUR") + new Money(1m, "GBP"));

    [Fact]
    public void Wire_names_match_contract()
    {
        Assert.Equal("REVIEW_REQUIRED", WireNames.ToUpperSnake(RunState.ReviewRequired));
        Assert.Equal("independent_ledger", WireNames.ToLowerSnake(ControlArea.IndependentLedger));
        Assert.Equal("safeguarding.adjustment.propose", WireNames.ToPermission(Permission.SafeguardingAdjustmentPropose));
    }
}
