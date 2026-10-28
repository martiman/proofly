using Proofly.Controls.Common;
using Proofly.Controls.Matching;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Tests.Matching;

public class MatchingControlTests
{
    private static readonly DateOnly D = new(2026, 10, 27);
    private static MatchItem L(string id, string? r, decimal a, string c = "XAA", DateOnly? d = null, bool dnull = false) =>
        new("transfer_instructions", id, r, new Money(a, c), dnull ? null : d ?? D);
    private static MatchItem B(string id, string? r, decimal a, string c = "XAA", DateOnly? d = null) =>
        new("banking_transactions", id, r, new Money(a, c), d ?? D);

    private static MatchingResult Run(MatchItem[] l, MatchItem[] r, MatchingOptions? o = null) =>
        MatchingControl.Evaluate(new MatchingInput([new MatchPair("p", l, r)]), o ?? new MatchingOptions());

    [Fact]
    public void ExactOneToOne()
    {
        var res = Run([L("1", "R1", 100m)], [B("b1", "R1", 100m)]);
        var g = Assert.Single(res.Groups);
        Assert.Equal(MatchGroupStatus.Matched, g.Status);
        Assert.Equal("1:1", g.Cardinality);
        Assert.Equal(MatchingCodes.ExactIdentifierMatch, g.RuleId);
        Assert.Equal(0m, g.ValueDifference!.Value.Amount);
        Assert.Empty(res.Control.Findings);
        Assert.Equal(ControlState.Passed, res.Control.State);
    }

    [Fact]
    public void CompositeWhenReferenceMissing()
    {
        var g = Assert.Single(Run([L("1", null, 50m)], [B("b1", "X", 50m)]).Groups);
        Assert.Equal(MatchingCodes.ExactCompositeMatch, g.RuleId);
    }

    [Fact]
    public void DateToleranceWithinAndOutsideWindow()
    {
        var ok = Assert.Single(Run([L("1", "R", 10m)], [B("b", "R", 10m, d: D.AddDays(2))]).Groups);
        Assert.Equal(MatchingCodes.DateToleranceMatch, ok.RuleId);
        Assert.Equal(2, ok.DateDifferenceDays);
        var res = Run([L("1", "R", 10m)], [B("b", "R", 10m, d: D.AddDays(3))]);
        Assert.Equal(2, res.Groups.Count);
        Assert.All(res.Groups, g => Assert.Equal(MatchingCodes.NoCandidate, g.ReasonCode));
    }

    [Fact]
    public void PerCurrencyDateWindow()
    {
        var o = new MatchingOptions { DateWindowDays = new Dictionary<string, int> { ["XBB"] = 0 } };
        var res = Run([L("1", "R", 10m, "XBB")], [B("b", "R", 10m, "XBB", D.AddDays(1))], o);
        Assert.DoesNotContain(res.Groups, g => g.Status == MatchGroupStatus.Matched);
    }

    [Fact]
    public void AmountToleranceBoundary()
    {
        var o = new MatchingOptions { AmountTolerance = new Dictionary<string, decimal> { ["XAA"] = 0.50m } };
        var ok = Assert.Single(Run([L("1", "R", 10m)], [B("b", "R", 10.50m)], o).Groups);
        Assert.Equal(MatchingCodes.AmountToleranceMatch, ok.RuleId);
        Assert.Equal(-0.50m, ok.ValueDifference!.Value.Amount);
        Assert.Equal(0.50m, ok.ToleranceUsed!.Value.Amount);
        var bad = Assert.Single(Run([L("1", "R", 10m)], [B("b", "R", 10.51m)], o).Groups);
        Assert.Equal(MatchGroupStatus.ValueMismatch, bad.Status);
    }

    [Fact]
    public void ValueMismatchIsFindingAndLeavesNoUnmatched()
    {
        var res = Run([L("1", "R", 600m)], [B("b", "R", 610m)]);
        var g = Assert.Single(res.Groups);
        Assert.Equal(MatchGroupStatus.ValueMismatch, g.Status);
        Assert.Equal(-10m, g.ValueDifference!.Value.Amount);
        var f = Assert.Single(res.Control.Findings);
        Assert.Equal(FindingCodes.ValueMismatch, f.ReasonCode);
        Assert.Equal(2, f.Records.Count);
        Assert.Equal(ControlState.ReviewRequired, res.Control.State);
    }

    [Fact]
    public void AmbiguousCandidatesAreNotResolved()
    {
        var res = Run([L("1", "R", 100m)], [B("b1", "R", 100m), B("b2", "R", 100m)]);
        var g = Assert.Single(res.Groups);
        Assert.Equal(MatchGroupStatus.Unmatched, g.Status);
        Assert.Equal(MatchingCodes.AmbiguousCandidates, g.ReasonCode);
        Assert.Equal(3, g.Candidates.Count);
        Assert.Contains("b1", g.Rationale);
        Assert.Single(res.Control.Findings);
    }

    [Fact]
    public void AmbiguousRecordsNeverFallToLooserRule()
    {
        // two identical instructions, one bank: exact is ambiguous; date/amount rules must not pick one
        var res = Run([L("1", "R", 100m), L("2", "R", 100m)], [B("b", "R", 100m)]);
        var g = Assert.Single(res.Groups);
        Assert.Equal(MatchingCodes.AmbiguousCandidates, g.ReasonCode);
        Assert.Equal("N:1", g.Cardinality);
    }

    [Fact]
    public void NoReuseAcrossRules()
    {
        // 1 matches b1 exactly; 2 would also fit b1 within date window but b1 is taken -> 2 unmatched
        var res = Run([L("1", "R", 100m), L("2", "R", 100m, d: D.AddDays(1))], [B("b1", "R", 100m)]);
        // exact step: only 1 is an edge (date equal) -> committed; 2 later has no free counterpart
        Assert.Equal(MatchGroupStatus.Matched, res.Groups.Single(g => g.Left.Any(x => x.SourceRowId == "1")).Status);
        Assert.Equal(MatchingCodes.NoCandidate, res.Groups.Single(g => g.Left.Any(x => x.SourceRowId == "2")).ReasonCode);
        Assert.Equal(res.Groups.SelectMany(g => g.Right).Count(), res.Groups.SelectMany(g => g.Right).Distinct().Count());
    }

    [Fact]
    public void ControlledManyToOneAndOneToMany()
    {
        var n1 = Assert.Single(Run([L("1", "S", 40m), L("2", "S", 60m)], [B("b", "S", 100m)]).Groups);
        Assert.Equal("N:1", n1.Cardinality);
        Assert.Equal(MatchingCodes.GroupedMatch, n1.RuleId);
        Assert.Equal(MatchGroupStatus.Matched, n1.Status);
        var one = Assert.Single(Run([L("1", "S", 100m)], [B("b1", "S", 30m), B("b2", "S", 70m)]).Groups);
        Assert.Equal("1:N", one.Cardinality);
    }

    [Fact]
    public void GroupedSumMismatchAndDisabled()
    {
        var bad = Assert.Single(Run([L("1", "S", 40m), L("2", "S", 60m)], [B("b", "S", 101m)]).Groups);
        Assert.Equal(MatchGroupStatus.ValueMismatch, bad.Status);
        Assert.Equal(-1m, bad.ValueDifference!.Value.Amount);
        var off = Run([L("1", "S", 40m), L("2", "S", 60m)], [B("b", "S", 100m)], new MatchingOptions { AllowGroupedMatches = false });
        Assert.DoesNotContain(off.Groups, g => g.Status == MatchGroupStatus.Matched);
    }

    [Fact]
    public void CurrenciesNeverNetted()
    {
        var res = Run([L("1", "R", 100m, "XAA")], [B("b", "R", 100m, "XBB")]);
        var g = Assert.Single(res.Groups);
        Assert.Equal(MatchingCodes.CurrencyMismatch, g.ReasonCode);
        Assert.Null(g.ValueDifference);
        // grouped sum across currencies is not formed
        var mixed = Run([L("1", "S", 40m, "XAA"), L("2", "S", 60m, "XBB")], [B("b", "S", 100m, "XAA")]);
        Assert.DoesNotContain(mixed.Groups, g2 => g2.Status == MatchGroupStatus.Matched);
    }

    [Fact]
    public void UnmatchedBothSides()
    {
        var res = Run([L("1", "A", 1m)], [B("b", "Z", 2m)]);
        Assert.Equal(2, res.Control.Findings.Count);
        Assert.Contains(res.Groups, g => g.Cardinality == "1:0");
        Assert.Contains(res.Groups, g => g.Cardinality == "0:1");
    }

    [Fact]
    public void DeterministicRegardlessOfInputOrder()
    {
        var l = new[] { L("1", "R1", 5m), L("2", "R2", 6m), L("3", "R3", 7m) };
        var r = new[] { B("b1", "R1", 5m), B("b2", "R2", 9m), B("b4", "Q", 1m) };
        var a = Run(l, r);
        var b = Run([.. l.Reverse()], [.. r.Reverse()]);
        Assert.Equal(a.Groups.Select(g => g.Id), b.Groups.Select(g => g.Id));
        Assert.Equal(a.Control.Findings.Select(f => f.Key), b.Control.Findings.Select(f => f.Key));
        Assert.Equal(a.Groups.Select(g => g.Id).Order(StringComparer.Ordinal), a.Groups.Select(g => g.Id));
    }

    [Fact]
    public void FromRecordsBuildsPairs()
    {
        var input = MatchingInput.FromRecords([], [], [], [], [], []);
        Assert.Empty(input.Pairs);
    }
}
