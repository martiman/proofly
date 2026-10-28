using Proofly.Controls.Common;
using Proofly.Controls.Safeguarding;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Tests.Safeguarding;

public class SafeguardingTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Day = new(2026, 10, 27);
    static readonly SafeguardingOptions Opt = new();

    static ClientObligationInput O(string id, string c, decimal a) => new(id, c, a);
    static ExternalBalanceInput B(string id, string c, decimal a, bool elig = true, int ageHours = 1) =>
        new(id, "X-" + id, new Money(a, c), Now.AddHours(-ageHours), elig, elig ? null : "not designated");

    static SafeguardingEvaluation Run(ClientObligationInput[] o, ExternalBalanceInput[] b, params AdjustmentInput[] a) =>
        SafeguardingCoverageControl.Evaluate(Now, Day, o, b, a, Opt);

    static CurrencyPosition P(SafeguardingEvaluation e, string c) => e.Positions.Single(p => p.Currency == c);

    [Fact]
    public void Surplus()
    {
        var e = Run([O("a", "AAA", 60), O("b", "AAA", 40)], [B("1", "AAA", 120)]);
        Assert.Equal(SafeguardingState.Surplus, P(e, "AAA").State);
        Assert.Equal(new Money(20, "AAA"), P(e, "AAA").Position);
        Assert.Equal(ControlState.Passed, e.Control.State);
    }

    [Fact]
    public void Exactly_covered()
    {
        var e = Run([O("a", "AAA", 100)], [B("1", "AAA", 100)]);
        Assert.Equal(SafeguardingState.Covered, P(e, "AAA").State);
    }

    [Fact]
    public void Shortfall_is_blocking_and_critical_when_large()
    {
        var e = Run([O("a", "AAA", 100)], [B("1", "AAA", 80)]);
        var f = Assert.Single(e.Control.Findings);
        Assert.Equal(FindingCodes.Shortfall, f.ReasonCode);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.True(f.Blocking);
        Assert.Equal(new Money(-20, "AAA"), f.Value);
        Assert.Equal(ControlState.Failed, e.Control.State);
    }

    [Fact]
    public void Small_shortfall_is_high()
    {
        var e = Run([O("a", "AAA", 1000)], [B("1", "AAA", 999)]);
        Assert.Equal(Severity.High, e.Control.Findings.Single().Severity);
    }

    [Fact]
    public void Missing_balance_is_not_zero_coverage()
    {
        var e = Run([O("a", "AAA", 100)], []);
        var f = Assert.Single(e.Control.Findings);
        Assert.Equal(SafeguardingCodes.ExternalBalanceMissing, f.ReasonCode);
        Assert.True(f.Blocking);
        Assert.Null(P(e, "AAA").Position);
        Assert.Equal(SafeguardingState.Qualified, P(e, "AAA").State);
    }

    [Fact]
    public void Stale_balance_flagged_and_excluded()
    {
        var e = Run([O("a", "AAA", 100)], [B("1", "AAA", 100, ageHours: 48)]);
        Assert.Contains(e.Control.Findings, f => f.ReasonCode == FindingCodes.ExternalBalanceStale && f.Severity == Severity.High);
        Assert.Contains(e.Control.Findings, f => f.ReasonCode == FindingCodes.Shortfall);
        Assert.False(P(e, "AAA").BalanceLines.Single().Included);
    }

    [Fact]
    public void Stale_boundary_is_not_stale()
    {
        var e = Run([O("a", "AAA", 100)], [B("1", "AAA", 100, ageHours: 24)]);
        Assert.Empty(e.Control.Findings);
    }

    [Fact]
    public void Negative_balance_invalid_and_excluded()
    {
        var e = Run([O("a", "AAA", 10)], [B("1", "AAA", -5), B("2", "AAA", 10)]);
        Assert.Contains(e.Control.Findings, f => f.ReasonCode == FindingCodes.InvalidValue);
        Assert.Equal(new Money(10, "AAA"), P(e, "AAA").EligibleTotal);
    }

    [Fact]
    public void Ineligible_balance_excluded_with_reason()
    {
        var e = Run([O("a", "AAA", 10)], [B("1", "AAA", 50, elig: false), B("2", "AAA", 10)]);
        var l = P(e, "AAA").BalanceLines.Single(x => x.SourceRowId == "1");
        Assert.False(l.Included);
        Assert.StartsWith("ACCOUNT_NOT_ELIGIBLE", l.ExclusionReason);
        Assert.Equal(new Money(10, "AAA"), P(e, "AAA").EligibleTotal);
    }

    [Fact]
    public void Only_approved_unexpired_adjustments_count()
    {
        var e = Run([O("a", "AAA", 100)], [B("1", "AAA", 90)],
            new AdjustmentInput("ok", new Money(10, "AAA"), AdjustmentStatus.Approved, Day),
            new AdjustmentInput("prop", new Money(500, "AAA"), AdjustmentStatus.Proposed),
            new AdjustmentInput("exp", new Money(500, "AAA"), AdjustmentStatus.Approved, Day.AddDays(-1)),
            new AdjustmentInput("neg", new Money(-3, "AAA"), AdjustmentStatus.Approved));
        Assert.Equal(new Money(7, "AAA"), P(e, "AAA").ApprovedAdjustments);
        Assert.Equal(new Money(-3, "AAA"), P(e, "AAA").Position);
        Assert.Contains(e.Control.Findings, f => f.ReasonCode == SafeguardingCodes.AdjustmentUnapproved);
    }

    [Fact]
    public void Currencies_are_isolated()
    {
        var e = Run([O("a", "AAA", 100), O("b", "BBB", 50)], [B("1", "AAA", 150), B("2", "BBB", 10), B("3", "CCC", 5)]);
        Assert.Equal(SafeguardingState.Surplus, P(e, "AAA").State);
        Assert.Equal(SafeguardingState.Shortfall, P(e, "BBB").State);
        Assert.Equal(SafeguardingState.Surplus, P(e, "CCC").State);
        Assert.Equal(new Money(5, "CCC"), P(e, "CCC").Position);
        Assert.Equal(new Money(-40, "BBB"), Assert.Single(e.Control.Findings).Value);
    }

    [Fact]
    public void Deterministic_and_sorted()
    {
        ClientObligationInput[] o = [O("a", "BBB", 50), O("b", "AAA", 50)];
        ExternalBalanceInput[] b = [B("1", "AAA", 1), B("2", "BBB", 1)];
        var x = Run(o, b); var y = Run(o.Reverse().ToArray(), b.Reverse().ToArray());
        Assert.Equal(x.Control.Findings.Select(f => f.Key), y.Control.Findings.Select(f => f.Key));
        Assert.Equal(x.Control.Findings.Select(f => f.Key).Order(StringComparer.Ordinal), x.Control.Findings.Select(f => f.Key));
    }
}
