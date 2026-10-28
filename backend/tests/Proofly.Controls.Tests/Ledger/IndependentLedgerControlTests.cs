using Proofly.Controls.Common;
using Proofly.Controls.Ledger;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Tests.Ledger;

public class IndependentLedgerControlTests
{
    private static readonly DateOnly Day = new(2026, 1, 15);
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static Money M(decimal a, string c = "XYZ") => new(a, c);
    private static LedgerEntry E(string id, DebitCredit dc, decimal amt, string? reference = null, string acct = "A1", string ccy = "XYZ",
        LedgerEntryType type = LedgerEntryType.Transfer, LedgerEntryStatus st = LedgerEntryStatus.Posted, int minute = 0, DateOnly? vd = null) =>
        new(id, acct, "LE1", T0.AddMinutes(minute), vd ?? Day, dc, M(amt, ccy), reference, type, st);
    private static TransferInstruction Tr(string id, decimal amt, TransferStatus st = TransferStatus.Settled, string acct = "A1", string? ext = null) =>
        new("row-" + id, id, acct, "LE1", "BANK_TRANSFER", M(amt), T0, Day, st, ext, null);
    private static AccountBalance Rec(decimal closing, string acct = "A1", string ccy = "XYZ", DateOnly? date = null) =>
        new(acct, "LE1", date ?? Day, AccountBalanceType.DailyRollforward, null, M(closing, ccy), T0);
    private static OpeningBalanceEvidence Op(decimal a, string acct = "A1", string ccy = "XYZ", string? support = "stmt-1") => new(acct, M(a, ccy), support);

    private static LedgerInput In(IEnumerable<OpeningBalanceEvidence> op, IEnumerable<LedgerEntry> e, IEnumerable<AccountBalance> rec,
        IEnumerable<TransferInstruction>? tr = null, IEnumerable<BankTransaction>? bank = null) =>
        new(Day, op.ToList(), e.ToList(), (bank ?? []).ToList(), (tr ?? []).ToList(), rec.ToList());

    private static IEnumerable<Finding> Of(LedgerEvaluation r, string code) => r.Result.Findings.Where(f => f.ReasonCode == code);

    [Fact]
    public void Balances_agree_passes()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(1000)], [E("L1", DebitCredit.Credit, 200), E("L2", DebitCredit.Debit, 50)], [Rec(1150)]));
        Assert.Empty(r.Result.Findings);
        Assert.Equal(ControlState.Passed, r.Result.State);
        var b = Assert.Single(r.Balances);
        Assert.Equal(M(1150), b.Independent);
        Assert.Equal(M(200), b.Credits);
        Assert.Equal(M(50), b.Debits);
        Assert.Equal(M(0), b.Difference);
    }

    [Fact]
    public void Missing_movement_difference_equals_movement_and_links_evidence()
    {
        var bank = new BankTransaction("BTX1", "EXT", T0, Day, M(70), DebitCredit.Debit, "BREF", BankTransactionStatus.Booked, T0);
        var r = IndependentLedgerControl.Calculate(In([Op(1000)], [], [Rec(1000)], [Tr("T1", 70, ext: "BREF")], [bank]));
        var miss = Assert.Single(Of(r, LedgerCodes.MovementMissing));
        Assert.Contains(new RecordRef("banking_transactions", "BTX1"), miss.Records);
        var diff = Assert.Single(Of(r, FindingCodes.BalanceDifference));
        Assert.Equal(M(-70), diff.Value);
        Assert.Equal(M(930), r.Balances[0].Independent);
        Assert.Contains(new RecordRef("transfer_instructions", "row-T1"), diff.Records);
        Assert.Equal(M(0), r.Balances[0].UnexplainedResidual);
        Assert.Equal(ControlState.Failed, r.Result.State);
    }

    [Fact]
    public void Duplicate_is_included_once_and_raised()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(100)], [E("L1", DebitCredit.Credit, 30), E("L1", DebitCredit.Credit, 30, minute: 1)], [Rec(160)]));
        Assert.Single(Of(r, FindingCodes.DuplicateRecord));
        Assert.Equal(M(130), r.Balances[0].Independent);
        var diff = Assert.Single(Of(r, FindingCodes.BalanceDifference));
        Assert.Equal(M(-30), diff.Value);
    }

    [Fact]
    public void Semantic_duplicate_with_other_id_is_detected()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(0)], [E("L1", DebitCredit.Credit, 30, "R1"), E("L2", DebitCredit.Credit, 30, "R1", minute: 1)], [Rec(30)]));
        Assert.Single(Of(r, FindingCodes.DuplicateRecord));
        Assert.Empty(Of(r, FindingCodes.BalanceDifference));
    }

    [Fact]
    public void Wrong_sign_is_flagged_and_corrected_from_evidence()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(500)], [E("L1", DebitCredit.Credit, 100, "T1")], [Rec(600)], [Tr("T1", 100)]));
        Assert.Single(Of(r, FindingCodes.InvalidSign));
        Assert.Equal(M(400), r.Balances[0].Independent);
        var diff = Assert.Single(Of(r, FindingCodes.BalanceDifference));
        Assert.Equal(M(-200), diff.Value);
        Assert.Equal(M(0), r.Balances[0].UnexplainedResidual);
    }

    [Fact]
    public void Posted_entry_for_rejected_transfer_is_excluded_and_explains_difference()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(500)], [E("L1", DebitCredit.Debit, 100, "T1")], [Rec(400)], [Tr("T1", 100, TransferStatus.Rejected)]));
        Assert.Equal(M(500), r.Balances[0].Independent);
        Assert.Contains(r.Balances[0].Exclusions, x => x.Reason == "TRANSFER_REJECTED");
        var diff = Assert.Single(Of(r, FindingCodes.BalanceDifference));
        Assert.Equal(M(100), diff.Value);
        Assert.Contains(new RecordRef("internal_ledger_entries", "L1"), diff.Records);
    }

    [Fact]
    public void Pending_and_reversed_entries_are_excluded()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(10)], [E("L1", DebitCredit.Credit, 5, st: LedgerEntryStatus.Pending), E("L2", DebitCredit.Credit, 7, st: LedgerEntryStatus.Reversed)], [Rec(10)]));
        Assert.Empty(r.Result.Findings);
        Assert.Equal(2, r.Balances[0].Exclusions.Count);
    }

    [Fact]
    public void Unsupported_opening_is_blocking_but_balance_is_calculated()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(10, support: " ")], [], [Rec(10)]));
        var f = Assert.Single(r.Result.Findings);
        Assert.Equal(LedgerCodes.OpeningBalanceUnsupported, f.ReasonCode);
        Assert.True(f.Blocking);
        Assert.False(r.Balances[0].OpeningSupported);
        Assert.Equal(M(10), r.Balances[0].Independent);
    }

    [Fact]
    public void Missing_opening_gives_no_pass()
    {
        var r = IndependentLedgerControl.Calculate(In([], [E("L1", DebitCredit.Credit, 5)], [Rec(5)]));
        Assert.Contains(r.Result.Findings, f => f.ReasonCode == LedgerCodes.OpeningBalanceMissing && f.Blocking);
        Assert.Null(r.Balances[0].Independent);
        Assert.Equal(ControlState.Failed, r.Result.State);
    }

    [Fact]
    public void Absent_recorded_balance_is_blocking_and_wrong_date_does_not_count()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(10)], [], [Rec(10, date: Day.AddDays(-1))]));
        var f = Assert.Single(r.Result.Findings);
        Assert.Equal(LedgerCodes.RecordedBalanceMissing, f.ReasonCode);
        Assert.Equal(M(10), r.Balances[0].Independent);
        Assert.Null(r.Balances[0].Recorded);
    }

    [Fact]
    public void Currencies_are_never_netted_and_scope_mismatch_is_reported()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(100, ccy: "XYZ"), Op(50, ccy: "ABC")],
            [E("L1", DebitCredit.Credit, 10, ccy: "ABC"), E("L2", DebitCredit.Credit, 9, ccy: "QRS")], [Rec(100), Rec(60, ccy: "ABC")]));
        Assert.Equal(2, r.Balances.Count);
        Assert.Equal(M(60, "ABC"), r.Balances.Single(b => b.Currency == "ABC").Independent);
        Assert.Equal(M(100), r.Balances.Single(b => b.Currency == "XYZ").Independent);
        Assert.Single(Of(r, LedgerCodes.CalculationScopeMismatch));
        Assert.Equal(ControlState.ReviewRequired, r.Result.State);
    }

    [Fact]
    public void Unmapped_account_future_value_date_and_nonpositive_amount()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(0)], [E("L1", DebitCredit.Credit, 5, acct: "ZZ"),
            E("L2", DebitCredit.Credit, 5, vd: Day.AddDays(1)), E("L3", DebitCredit.Credit, -5)], [Rec(0)]));
        Assert.Single(Of(r, LedgerCodes.MovementUnmapped));
        Assert.Single(Of(r, LedgerCodes.InvalidValueDate));
        Assert.Single(Of(r, FindingCodes.InvalidSign));
        Assert.Equal(M(0), r.Balances[0].Independent);
    }

    [Fact]
    public void Tolerance_hides_finding_not_difference()
    {
        var r = IndependentLedgerControl.Calculate(In([Op(10)], [], [Rec(10.4m)]), new LedgerOptions { Tolerance = 0.5m });
        Assert.Empty(r.Result.Findings);
        Assert.Equal(M(-0.4m), r.Balances[0].Difference);
    }

    [Fact]
    public void Adjustments_are_reported_separately_and_result_is_order_independent()
    {
        var es = new[] { E("L1", DebitCredit.Credit, 10), E("L2", DebitCredit.Credit, 4, type: LedgerEntryType.Adjustment, minute: 1), E("L3", DebitCredit.Debit, 3, minute: 2) };
        var a = IndependentLedgerControl.Calculate(In([Op(0)], es, [Rec(0)]));
        var b = IndependentLedgerControl.Calculate(In([Op(0)], es.Reverse(), [Rec(0)]));
        Assert.Equal(M(4), a.Balances[0].Adjustments);
        Assert.Equal(M(10), a.Balances[0].Credits);
        Assert.Equal(a.Result.Findings.Select(f => f.Key), b.Result.Findings.Select(f => f.Key));
        Assert.Equal(a.Result.Findings.Select(f => f.Description), b.Result.Findings.Select(f => f.Description));
    }
}
