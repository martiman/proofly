using Proofly.Controls.Common;
using Proofly.Controls.Completeness;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Tests.Completeness;

public class DataCompletenessTests
{
    static readonly DateOnly Day = new(2026, 1, 5);
    static readonly DateTimeOffset T = new(2026, 1, 5, 6, 0, 0, TimeSpan.Zero);

    static DeliveryInfo D(string id, string src, string fp = null!, int? decl = 2, int act = 2,
        Money[]? dt = null, Money[]? at = null, DeliveryState st = DeliveryState.Received, string? sup = null,
        bool schema = true, SequenceStatus seq = SequenceStatus.NotApplicable, DateTimeOffset? exp = null, DateTimeOffset? rec = null) =>
        new(id, src, st, fp ?? "fp-" + id, decl, act, dt ?? [], at ?? [], schema, seq, 0, sup, exp ?? T, rec ?? T);

    static ControlResult Run(DeliveryInfo[] ds, string[]? mand = null, RejectedRecord[]? rej = null, CompletenessOptions? o = null) =>
        DataCompletenessControl.Evaluate(new CompletenessInput(Day, mand ?? ["a", "b"], ds, rej ?? []), o);

    static string[] Codes(ControlResult r) => r.Findings.Select(f => f.ReasonCode).ToArray();

    [Fact]
    public void Complete_passes()
    {
        var r = Run([D("1", "a"), D("2", "b")]);
        Assert.Equal(ControlState.Passed, r.State);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Missing_mandatory_source_fails_and_blocks()
    {
        var r = Run([D("1", "a")]);
        var f = Assert.Single(r.Findings);
        Assert.Equal(CompletenessCodes.SourceMissing, f.ReasonCode);
        Assert.True(f.Blocking);
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal(ControlState.Failed, r.State);
    }

    [Fact]
    public void Non_mandatory_absent_is_fine_and_rejected_only_is_blocking()
    {
        Assert.Equal(ControlState.Passed, Run([D("1", "a")], ["a"]).State);
        var r = Run([D("1", "a"), D("2", "b", st: DeliveryState.Rejected)]);
        Assert.Equal([CompletenessCodes.DeliveryRejected], Codes(r));
        Assert.Equal(ControlState.Failed, r.State);
    }

    [Fact]
    public void Late_delivery_qualifies()
    {
        var r = Run([D("1", "a"), D("2", "b", rec: T.AddMinutes(1))]);
        Assert.Equal([CompletenessCodes.SourceLate], Codes(r));
        Assert.Equal(ControlState.Qualified, r.State);
        Assert.Empty(Run([D("1", "a"), D("2", "b", rec: T)]).Findings); // boundary: on time
    }

    [Fact]
    public void Duplicate_delivery_is_flagged_once_and_not_recounted()
    {
        var r = Run([D("1", "a", "x"), D("2", "a", "x", decl: 9, rec: T.AddHours(1)), D("3", "b")]);
        Assert.Contains(r.Findings, f => f.ReasonCode == CompletenessCodes.DuplicateDelivery && f.Records[0].SourceRowId == "2");
        Assert.DoesNotContain(r.Findings, f => f.ReasonCode == FindingCodes.ControlCountMismatch);
        Assert.Equal(ControlState.Qualified, r.State);
    }

    [Fact]
    public void Count_mismatch_blocks_by_default_and_can_qualify()
    {
        var ds = new[] { D("1", "a", decl: 9, act: 10), D("2", "b") };
        var r = Run(ds);
        Assert.Equal([FindingCodes.ControlCountMismatch], Codes(r));
        Assert.Equal(ControlState.Failed, r.State);
        var q = Run(ds, o: new CompletenessOptions { CountMismatchBlockingBySource = new Dictionary<string, bool> { ["a"] = false } });
        Assert.Equal(ControlState.Qualified, q.State);
    }

    [Fact]
    public void Value_mismatch_is_per_currency_and_never_netted()
    {
        var r = Run([D("1", "a",
            dt: [new(10m, "GBP"), new(5m, "EUR"), new(1m, "JPY")],
            at: [new(9m, "GBP"), new(6m, "EUR"), new(1m, "JPY")]), D("2", "b")]);
        Assert.Equal(2, r.Findings.Count);
        Assert.Equal(new Money(-1m, "GBP"), r.Findings.Single(f => f.Key.EndsWith(":GBP")).Value);
        Assert.Equal(new Money(1m, "EUR"), r.Findings.Single(f => f.Key.EndsWith(":EUR")).Value);
        Assert.All(r.Findings, f => Assert.Equal(FindingCodes.ControlValueMismatch, f.ReasonCode));
        Assert.Equal(ControlState.Qualified, r.State);
        Assert.Equal(ControlState.Failed, Run([D("1", "a", dt: [new(1m, "GBP")], at: [new(2m, "GBP")]), D("2", "b")],
            o: new CompletenessOptions { ValueMismatchBlocking = true }).State);
    }

    [Fact]
    public void Currency_declared_but_absent_in_rows_is_a_mismatch_and_no_totals_skips()
    {
        Assert.Single(Run([D("1", "a", dt: [new(3m, "EUR")], at: []), D("2", "b")]).Findings);
        Assert.Empty(Run([D("1", "a", at: [new(3m, "EUR")]), D("2", "b")]).Findings);
    }

    [Fact]
    public void Superseded_with_correction_is_informational_without_is_blocking()
    {
        var ok = Run([D("1", "a", st: DeliveryState.Superseded, decl: 9), D("2", "a", sup: "1"), D("3", "b")]);
        Assert.Equal([CompletenessCodes.DeliverySuperseded], Codes(ok));
        Assert.Equal(Severity.Low, ok.Findings[0].Severity);
        Assert.Equal(ControlState.Passed, ok.State);
        var bad = Run([D("1", "a", st: DeliveryState.Superseded), D("3", "b")]);
        Assert.Contains(CompletenessCodes.CorrectionLinkMissing, Codes(bad));
        Assert.Contains(CompletenessCodes.SourceMissing, Codes(bad));
        Assert.Equal(ControlState.Failed, bad.State);
    }

    [Fact]
    public void Schema_and_sequence_findings()
    {
        var r = Run([D("1", "a", schema: false), D("2", "b", seq: SequenceStatus.Gap)]);
        Assert.Equal([CompletenessCodes.SchemaUnsupported, CompletenessCodes.SequenceGap], Codes(r).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(ControlState.Failed, r.State);
    }

    [Fact]
    public void Rejected_records_are_visible_per_record_and_qualify()
    {
        var rej = new[]
        {
            new RejectedRecord("a", "1", 3, "SRC-9", FindingCodes.RequiredFieldMissing, "client_account_id is empty"),
            new RejectedRecord("a", "1", 4, null, FindingCodes.InvalidValue, "currency EURO"),
            new RejectedRecord("a", "1", 5, "SRC-10", FindingCodes.DuplicateRecord, "duplicate id"),
        };
        var r = Run([D("1", "a"), D("2", "b")], rej: rej);
        Assert.Equal(3, r.Findings.Count);
        Assert.Equal(ControlState.Qualified, r.State);
        Assert.All(r.Findings, f => { Assert.Equal(Severity.High, f.Severity); Assert.False(f.Blocking); Assert.Single(f.Records); });
        Assert.Contains(r.Findings, f => f.Key == "record:INVALID_VALUE:a:1:row4");
    }

    [Fact]
    public void Deterministic_and_sorted()
    {
        var ds = new[] { D("2", "b", decl: 1), D("1", "a", decl: 5) };
        var a = Run(ds); var b = Run(ds.Reverse().ToArray());
        Assert.Equal(a.Findings.Select(f => f.Key), b.Findings.Select(f => f.Key));
        Assert.Equal(a.Findings.Select(f => f.Key).OrderBy(x => x, StringComparer.Ordinal), a.Findings.Select(f => f.Key));
    }

    [Fact]
    public void Gate_stops_unqualified_pass_downstream()
    {
        var comp = Run([D("1", "a")]);
        var down = ControlResult.From(ControlArea.Matching, "m-1", []);
        Assert.Equal(ControlState.Passed, down.State);
        var g = DataCompletenessControl.ApplyGate(down, comp);
        Assert.Equal(ControlState.Qualified, g.State);
        Assert.Contains(g.Findings, f => f.ReasonCode == CompletenessCodes.CompletenessQualified);
        var healthy = Run([D("1", "a"), D("2", "b")]);
        Assert.Equal(ControlState.Passed, DataCompletenessControl.ApplyGate(down, healthy).State);
        var failedDown = ControlResult.From(ControlArea.Matching, "m-1", [g.Findings[0] with { Blocking = true }]);
        Assert.Equal(ControlState.Failed, DataCompletenessControl.ApplyGate(failedDown, comp).State);
    }
}
