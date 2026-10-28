using Proofly.Controls.Common;
using Proofly.Controls.Lifecycle;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Tests.Lifecycle;

public class TransactionLifecycleControlTests
{
    private static readonly DateOnly Biz = new(2026, 10, 28); // Wednesday
    private static DateTimeOffset At(int day, int hour = 9) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private static TransferInstruction Trf(string id, TransferStatus st, int day = 28, string currency = "GBP") =>
        new("S-" + id, id, "CA", "LE", "BANK_TRANSFER", new Money(100m, currency), At(day), null, st, "REF-" + id, null);
    private static LedgerEntry Led(string id, string reference, LedgerEntryType type = LedgerEntryType.Transfer,
        DebitCredit dc = DebitCredit.Debit, string currency = "GBP") =>
        new(id, "CA", "LE", At(28), new DateOnly(2026, 10, 28), dc, new Money(100m, currency), reference, type, LedgerEntryStatus.Posted);
    private static BankTransaction Bank(string id, string reference, DebitCredit dc = DebitCredit.Debit, decimal amt = 100m,
        string currency = "GBP", int day = 28) =>
        new(id, "BANK", At(day), new DateOnly(2026, 10, day), new Money(amt, currency), dc, reference, BankTransactionStatus.Booked, At(28));
    private static DirectDebitEvent Dd(string row, string id, DirectDebitEventType t, int day, int hour = 9, string mandate = "M1") =>
        new(row, id, mandate, "CA", "LE", new Money(50m, "EUR"), new DateOnly(2026, 10, day), t, At(day, hour),
            Enum.Parse<DirectDebitStatus>(t.ToString()), t == DirectDebitEventType.Returned ? "AC04" : null, null);
    private static CardEvent Card(string row, string tx, CardEventType t, int day, int hour = 9, string? settle = null, string? orig = null) =>
        new(row, row, tx, "CA", "LE", "TOK", t, At(day, hour), new Money(10m, "JPY"), DebitCredit.Debit, "M", settle, orig,
            t switch { CardEventType.Authorization => CardEventStatus.Authorized, CardEventType.Presentment => CardEventStatus.Presented,
                CardEventType.Reversal => CardEventStatus.Reversed, CardEventType.Refund => CardEventStatus.Refunded, _ => CardEventStatus.ChargedBack });
    private static CardSettlement Settle(string id, int day = 28) =>
        new("S-" + id, id, "P", "LE", new DateOnly(2026, 10, day), new Money(10m, "JPY"), Money.Zero("JPY"), Money.Zero("JPY"),
            new Money(10m, "JPY"), "BANK", SettlementStatus.Settled);

    private static LifecycleResult Run(
        IEnumerable<TransferInstruction>? t = null, IEnumerable<DirectDebitEvent>? d = null, IEnumerable<CardEvent>? c = null,
        IEnumerable<CardSettlement>? s = null, IEnumerable<LedgerEntry>? l = null, IEnumerable<BankTransaction>? b = null,
        LifecycleOptions? o = null) =>
        TransactionLifecycleControl.EvaluateDetailed(new LifecycleInput(Biz, [.. t ?? []], [.. d ?? []], [.. c ?? []],
            [.. s ?? []], [.. l ?? []], [.. b ?? []]), o);

    private static string[] Codes(LifecycleResult r) => [.. r.Control.Findings.Select(f => f.ReasonCode)];
    private static LifecycleVerdict V(LifecycleResult r, string id) => r.Verdicts.Single(v => v.TransactionId == id);

    [Fact]
    public void Transfer_posted_and_booked_is_completed()
    {
        var r = Run(t: [Trf("T1", TransferStatus.Settled)], l: [Led("L1", "T1")], b: [Bank("B1", "REF-T1")]);
        Assert.Empty(r.Control.Findings);
        Assert.Equal(ControlState.Passed, r.Control.State);
        Assert.Equal(LifecycleState.Completed, V(r, "T1").State);
    }

    [Fact]
    public void Transfer_without_bank_evidence_inside_window_is_pending_not_failed()
    {
        var r = Run(t: [Trf("T1", TransferStatus.Settled)], l: [Led("L1", "T1")]);
        Assert.Empty(r.Control.Findings);
        Assert.Equal(LifecycleState.Pending, V(r, "T1").State);
        Assert.Equal(LifecycleTiming.Within, V(r, "T1").Timing);
    }

    [Fact]
    public void Transfer_window_boundary_is_inclusive_and_beyond_is_a_breach()
    {
        // instructed Tue 27 -> age 1 business day == window 1 -> still pending
        var ok = Run(t: [Trf("T1", TransferStatus.Settled, 27)], l: [Led("L1", "T1")]);
        Assert.Empty(ok.Control.Findings);
        // instructed Mon 26 -> age 2 > 1
        var late = Run(t: [Trf("T1", TransferStatus.Settled, 26)], l: [Led("L1", "T1")]);
        Assert.Equal([LifecycleCodes.MissingExternalOutcome], Codes(late));
        Assert.Equal(LifecycleTiming.Overdue, V(late, "T1").Timing);
    }

    [Fact]
    public void Weekend_and_holidays_do_not_count()
    {
        // Fri 23 -> Wed 28 is 3 business days; with Mon 26 a holiday it is 2.
        var o = new LifecycleOptions { TransferWindowBusinessDays = 2, Holidays = new HashSet<DateOnly> { new(2026, 10, 26) } };
        var r = Run(t: [Trf("T1", TransferStatus.Settled, 23)], l: [Led("L1", "T1")], o: o);
        Assert.Empty(r.Control.Findings);
    }

    [Fact]
    public void Settled_transfer_missing_ledger_after_window_is_missing_posting()
    {
        var r = Run(t: [Trf("T1", TransferStatus.Settled, 20)], b: [Bank("B1", "REF-T1", day: 21)]);
        Assert.Equal([LifecycleCodes.MissingInternalPosting], Codes(r));
    }

    [Fact]
    public void Late_bank_outcome_is_reported()
    {
        var r = Run(t: [Trf("T1", TransferStatus.Settled, 20)], l: [Led("L1", "T1")], b: [Bank("B1", "REF-T1", day: 27)]);
        Assert.Equal([LifecycleCodes.LateOutcome], Codes(r));
        Assert.False(r.Control.Findings[0].Blocking);
    }

    [Fact]
    public void Stalled_submitted_transfer_only_after_window()
    {
        Assert.Empty(Run(t: [Trf("T1", TransferStatus.Submitted, 27)]).Control.Findings);
        Assert.Equal([LifecycleCodes.Stalled], Codes(Run(t: [Trf("T1", TransferStatus.Submitted, 20)])));
    }

    [Fact]
    public void Rejected_transfer_with_posted_debit_is_invalid_state_and_blocks()
    {
        var r = Run(t: [Trf("T1", TransferStatus.Rejected)], l: [Led("L1", "T1")]);
        Assert.Equal([FindingCodes.InvalidState], Codes(r));
        Assert.Equal(ControlState.Failed, r.Control.State);
        // a reversing entry makes it clean
        var ok = Run(t: [Trf("T1", TransferStatus.Rejected)], l: [Led("L1", "T1"), Led("L2", "T1", LedgerEntryType.Reversal, DebitCredit.Credit)]);
        Assert.Empty(ok.Control.Findings);
    }

    [Fact]
    public void Ledger_transfer_for_unknown_instruction_is_missing_instruction()
    {
        var r = Run(l: [Led("L1", "T-404")]);
        Assert.Equal([FindingCodes.MissingInstruction], Codes(r));
    }

    [Fact]
    public void Direct_debit_collected_and_posted_completes()
    {
        var r = Run(d: [Dd("D1", "DD1", DirectDebitEventType.Submitted, 27), Dd("D2", "DD1", DirectDebitEventType.Settled, 28)],
            l: [Led("L1", "DD1", LedgerEntryType.DirectDebit, DebitCredit.Credit, "EUR")]);
        Assert.Empty(r.Control.Findings);
        Assert.Equal("Settled", V(r, "DD1").StageReached);
    }

    [Fact]
    public void Returned_direct_debit_without_reversal_is_return_not_posted()
    {
        var evs = new[] { Dd("D1", "DD2", DirectDebitEventType.Submitted, 27), Dd("D2", "DD2", DirectDebitEventType.Settled, 27, 10),
            Dd("D3", "DD2", DirectDebitEventType.Returned, 28) };
        var r = Run(d: evs);
        Assert.Equal([FindingCodes.ReturnNotPosted], Codes(r));
        Assert.True(r.Control.Findings[0].Blocking);
        var ok = Run(d: evs, l: [Led("L1", "DD2", LedgerEntryType.Reversal, DebitCredit.Credit, "EUR")]);
        Assert.Empty(ok.Control.Findings);
    }

    [Fact]
    public void Direct_debit_unknown_instruction_missing_mandate_and_bad_transition()
    {
        Assert.Equal([FindingCodes.MissingInstruction], Codes(Run(d: [Dd("D1", "DD9", DirectDebitEventType.Returned, 28)])
            ).Where(x => x == FindingCodes.MissingInstruction).ToArray());
        Assert.Contains(FindingCodes.MissingAuthorization,
            Codes(Run(d: [Dd("D1", "DD3", DirectDebitEventType.Submitted, 28, mandate: " ")])));
        var bad = Run(d: [Dd("D1", "DD4", DirectDebitEventType.Submitted, 27), Dd("D2", "DD4", DirectDebitEventType.Returned, 28)]);
        Assert.Contains(LifecycleCodes.InvalidTransition, Codes(bad));
    }

    [Fact]
    public void Direct_debit_settled_and_rejected_conflict()
    {
        var r = Run(d: [Dd("D1", "DD5", DirectDebitEventType.Submitted, 27), Dd("D2", "DD5", DirectDebitEventType.Settled, 28),
            Dd("D3", "DD5", DirectDebitEventType.Rejected, 28, 10)]);
        Assert.Contains(LifecycleCodes.ConflictingOutcomes, Codes(r));
    }

    [Fact]
    public void Direct_debit_pending_inside_window_and_stalled_beyond()
    {
        Assert.Equal(LifecycleState.Pending, V(Run(d: [Dd("D1", "DD6", DirectDebitEventType.Submitted, 27)]), "DD6").State);
        var late = Run(d: [Dd("D1", "DD6", DirectDebitEventType.Submitted, 20)]);
        Assert.Equal([LifecycleCodes.Stalled], Codes(late));
    }

    [Fact]
    public void Card_full_journey_is_completed()
    {
        var r = Run(c: [Card("K1", "C1", CardEventType.Authorization, 27), Card("K2", "C1", CardEventType.Presentment, 28, settle: "S1")],
            s: [Settle("S1")], b: [Bank("B1", "S1", DebitCredit.Credit, 10m, "JPY")]);
        Assert.Empty(r.Control.Findings);
        Assert.Equal("Settled to bank", V(r, "C1").StageReached);
        Assert.Equal(LifecycleState.Completed, V(r, "C1").State);
    }

    [Fact]
    public void Card_presentment_without_authorization_unknown_settlement_and_options()
    {
        var evs = new[] { Card("K1", "C2", CardEventType.Presentment, 28, settle: "S404") };
        var r = Run(c: evs);
        Assert.Equal([FindingCodes.MissingAuthorization, FindingCodes.MissingSettlement], Codes(r).Order().ToArray());
        var relaxed = Run(c: evs, o: new LifecycleOptions { RequireCardAuthorization = false });
        Assert.Equal([FindingCodes.MissingSettlement], Codes(relaxed));
    }

    [Fact]
    public void Card_presentment_without_settlement_id_pending_then_breach()
    {
        var pend = Run(c: [Card("K1", "C3", CardEventType.Authorization, 27), Card("K2", "C3", CardEventType.Presentment, 28)]);
        Assert.Empty(pend.Control.Findings);
        Assert.Equal(LifecycleState.Pending, V(pend, "C3").State);
        var late = Run(c: [Card("K1", "C3", CardEventType.Authorization, 20), Card("K2", "C3", CardEventType.Presentment, 21)]);
        Assert.Equal([FindingCodes.MissingSettlement], Codes(late));
    }

    [Fact]
    public void Card_settlement_without_bank_credit_pending_then_missing_external_outcome()
    {
        var pres = new[] { Card("K1", "C4", CardEventType.Authorization, 27), Card("K2", "C4", CardEventType.Presentment, 27, settle: "S2") };
        Assert.Empty(Run(c: pres, s: [Settle("S2", 28)]).Control.Findings);
        Assert.Equal([LifecycleCodes.MissingExternalOutcome], Codes(Run(c: pres, s: [Settle("S2", 21)])));
        // wrong amount is not a bank credit for this settlement
        Assert.Equal([LifecycleCodes.MissingExternalOutcome],
            Codes(Run(c: pres, s: [Settle("S2", 21)], b: [Bank("B1", "S2", DebitCredit.Credit, 9m, "JPY")])));
    }

    [Fact]
    public void Card_refund_and_chargeback_need_a_known_presented_original()
    {
        var unknown = Run(c: [Card("K1", "C5", CardEventType.Refund, 28, settle: "S3", orig: "C-404")], s: [Settle("S3")],
            b: [Bank("B1", "S3", DebitCredit.Credit, 10m, "JPY")]);
        Assert.Equal([FindingCodes.MissingOriginalTransaction], Codes(unknown));
        var notPresented = Run(c: [Card("K1", "C6", CardEventType.Authorization, 28), Card("K2", "C7", CardEventType.Chargeback, 28, settle: "S3", orig: "C6")],
            s: [Settle("S3")], b: [Bank("B1", "S3", DebitCredit.Credit, 10m, "JPY")]);
        Assert.Equal([FindingCodes.InvalidState], Codes(notPresented));
    }

    [Fact]
    public void Card_illegal_transitions()
    {
        var r = Run(c: [Card("K1", "C8", CardEventType.Authorization, 28, 8), Card("K2", "C8", CardEventType.Presentment, 28, 9),
            Card("K3", "C8", CardEventType.Reversal, 28, 10, orig: "C8")]);
        Assert.Contains(LifecycleCodes.InvalidTransition, Codes(r));
        var ok = Run(c: [Card("K1", "C9", CardEventType.Authorization, 28, 8), Card("K3", "C9", CardEventType.Reversal, 28, 10, orig: "C9")]);
        Assert.Empty(ok.Control.Findings);
    }

    [Fact]
    public void Card_authorization_pending_then_stalled()
    {
        Assert.Empty(Run(c: [Card("K1", "C10", CardEventType.Authorization, 27)]).Control.Findings);
        Assert.Equal([LifecycleCodes.Stalled], Codes(Run(c: [Card("K1", "C10", CardEventType.Authorization, 10)])));
    }

    [Fact]
    public void Currencies_are_never_netted_and_output_is_deterministic()
    {
        var t = new[] { Trf("T1", TransferStatus.Settled, 20, "JPY"), Trf("T2", TransferStatus.Settled, 20, "GBP") };
        var l = new[] { Led("L1", "T1", currency: "JPY"), Led("L2", "T2") };
        var a = Run(t: t, l: l);
        var b = Run(t: t.AsEnumerable().Reverse(), l: l.AsEnumerable().Reverse());
        Assert.Equal(a.Control.Findings.Select(f => f.Key), b.Control.Findings.Select(f => f.Key));
        Assert.Equal(new[] { "JPY", "GBP" }.Order(), a.Control.Findings.Select(f => f.Value!.Value.Currency).Order());
        Assert.All(a.Control.Findings, f => Assert.Equal(ControlArea.TransactionLifecycle, f.Area));
    }
}
