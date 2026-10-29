using Proofly.Controls.Common;
using Proofly.Controls.Lifecycle;
using Proofly.Controls.Matching;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Tests.Matching;

/// <summary>
/// Pair construction from canonical records and the primary record of a finding (what an exception is attributed to). These are the pipeline
/// rules the synthetic-dataset reproduction (issue #38) depends on; see docs/testing/dataset-findings-report.md.
/// </summary>
public class MatchingInputFromRecordsTests
{
    private static readonly DateOnly D = new(2026, 10, 27);
    private static DateTimeOffset At(int hour = 9) => new(2026, 10, 27, hour, 0, 0, TimeSpan.Zero);

    private static TransferInstruction Trf(string id, decimal amt, TransferStatus st = TransferStatus.Submitted) =>
        new("S-" + id, id, "CA", "LE", "BANK_TRANSFER", new Money(amt, "GBP"), At(), D, st, "BANKREF-" + id, null);
    private static LedgerEntry Led(string id, string reference, decimal amt) =>
        new(id, "CA", "LE", At(), D, DebitCredit.Debit, new Money(amt, "GBP"), reference, LedgerEntryType.Transfer, LedgerEntryStatus.Posted);
    private static BankTransaction Bank(string id, string reference, decimal amt) =>
        new(id, "BANK", At(), D, new Money(amt, "GBP"), DebitCredit.Debit, reference, BankTransactionStatus.Booked, At());
    private static DirectDebitEvent Dd(string row, string id, DirectDebitEventType t) =>
        new(row, id, "M1", "CA", "LE", new Money(120m, "GBP"), D, t, At(), Enum.Parse<DirectDebitStatus>(t.ToString()), null, null);
    private static CardEvent Card(string row, string tx, CardEventType t, decimal amt) =>
        new(row, row, tx, "CA", "LE", "TOK", t, At(), new Money(amt, "GBP"), DebitCredit.Debit, "M", null, null,
            t == CardEventType.Authorization ? CardEventStatus.Authorized : CardEventStatus.Presented);

    private static MatchingResult Match(
        TransferInstruction[]? trf = null, LedgerEntry[]? led = null, DirectDebitEvent[]? dd = null, CardEvent[]? cards = null, BankTransaction[]? bank = null) =>
        MatchingControl.Evaluate(MatchingInput.FromRecords(trf ?? [], led ?? [], dd ?? [], [], cards ?? [], bank ?? []), new MatchingOptions());

    [Fact]
    public void A_ledger_entry_of_a_known_instruction_matches_the_bank_through_the_instructions_external_reference()
    {
        var r = Match(trf: [Trf("TRF-1", 100m)], led: [Led("LED-1", "TRF-1", 100m)], bank: [Bank("BTX-1", "BANKREF-TRF-1", 100m)]);
        Assert.Empty(r.Control.Findings);
        Assert.Equal(2, r.Groups.Count(g => g.Status == MatchGroupStatus.Matched));
    }

    [Fact]
    public void A_ledger_reference_that_names_no_instruction_is_used_as_it_is()
    {
        var r = Match(led: [Led("LED-1", "DD-9", 100m)], bank: [Bank("BTX-1", "DD-9", 100m)]);
        Assert.Empty(r.Control.Findings);
    }

    [Fact]
    public void Only_the_settled_collection_of_a_direct_debit_is_matched_to_the_bank_so_submission_and_settlement_are_not_ambiguous()
    {
        var r = Match(dd: [Dd("D-1", "DD-1", DirectDebitEventType.Submitted), Dd("D-2", "DD-1", DirectDebitEventType.Settled)], bank: [Bank("BTX-1", "DD-1", 120m)]);
        Assert.Empty(r.Control.Findings);
        Assert.Single(r.Groups, g => g.Status == MatchGroupStatus.Matched && g.Left.Single().SourceRowId == "D-2");
    }

    [Fact]
    public void Rejected_and_cancelled_instructions_expect_no_bank_movement()
    {
        var r = Match(trf: [Trf("TRF-1", 100m, TransferStatus.Rejected), Trf("TRF-2", 50m, TransferStatus.Cancelled)]);
        Assert.Empty(r.Groups);
    }

    [Fact]
    public void A_card_authorisation_is_compared_with_the_presentment_of_the_same_transaction_and_the_presentment_is_the_primary_record()
    {
        var r = Match(cards: [Card("K-1", "CTX-1", CardEventType.Authorization, 60m), Card("K-2", "CTX-1", CardEventType.Presentment, 65m)]);
        var f = Assert.Single(r.Control.Findings);
        Assert.Equal(FindingCodes.ValueMismatch, f.ReasonCode);
        Assert.Equal(new RecordRef("card_transactions", "K-2"), f.Primary);
        Assert.Equal(-5m, f.Value!.Value.Amount);
    }

    [Fact]
    public void An_authorisation_without_a_presentment_or_the_reverse_is_the_lifecycle_controls_finding_not_a_matching_one()
    {
        Assert.Empty(Match(cards: [Card("K-1", "CTX-1", CardEventType.Authorization, 60m)]).Groups);
        Assert.DoesNotContain(Match(cards: [Card("K-2", "CTX-2", CardEventType.Presentment, 65m)]).Groups, g => g.Pair == MatchingInput.AuthorisationToPresentment);
    }

    [Fact]
    public void A_bank_record_with_a_value_mismatch_in_one_pair_is_not_also_reported_as_unmatched_in_the_other_pairs()
    {
        var r = Match(trf: [Trf("TRF-1", 100m)], led: [Led("LED-1", "TRF-1", 100m)], bank: [Bank("BTX-1", "BANKREF-TRF-1", 110m)]);
        Assert.DoesNotContain(r.Control.Findings, f => f.ReasonCode == "NO_CANDIDATE" && f.Records.Any(x => x.SourceName == "banking_transactions"));
        // The instruction and the ledger entry both disagree with the same bank movement: one finding, in the first pair by name.
        var f = Assert.Single(r.Control.Findings, x => x.ReasonCode == FindingCodes.ValueMismatch);
        Assert.Equal("banking_transactions", f.Primary!.SourceName);
    }
}

/// <summary>A finding names the record it is about (<see cref="Finding.Primary"/>), whatever the order of its supporting records.</summary>
public class FindingPrimaryRecordTests
{
    private static readonly DateOnly Biz = new(2026, 10, 28);
    private static DateTimeOffset At(int day, int hour = 9) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_posted_debit_of_a_rejected_transfer_is_attributed_to_the_ledger_entry_not_the_transfer()
    {
        var t = new TransferInstruction("S-T1", "TRF-1", "CA", "LE", "BANK_TRANSFER", new Money(100m, "GBP"), At(28), null, TransferStatus.Rejected, "REF-T1", null);
        var l = new LedgerEntry("LED-1", "CA", "LE", At(28), Biz, DebitCredit.Debit, new Money(100m, "GBP"), "TRF-1", LedgerEntryType.Transfer, LedgerEntryStatus.Posted);
        var f = Assert.Single(TransactionLifecycleControl.Evaluate(new LifecycleInput(Biz, [t], [], [], [], [l], [])).Findings);
        Assert.Equal(FindingCodes.InvalidState, f.ReasonCode);
        Assert.Equal(new RecordRef("internal_ledger_entries", "LED-1"), f.Primary);
    }

    [Fact]
    public void A_return_not_posted_is_attributed_to_the_returned_event_not_the_first_event_of_the_debit()
    {
        DirectDebitEvent E(string row, DirectDebitEventType type, int hour) =>
            new(row, "DD-1", "M1", "CA", "LE", new Money(50m, "GBP"), Biz, type, At(28, hour), Enum.Parse<DirectDebitStatus>(type.ToString()), null, null);
        var r = TransactionLifecycleControl.Evaluate(new LifecycleInput(Biz,
            [], [E("D-1", DirectDebitEventType.Submitted, 8), E("D-2", DirectDebitEventType.Settled, 9), E("D-3", DirectDebitEventType.Returned, 10)], [], [], [], []));
        var f = Assert.Single(r.Findings, x => x.ReasonCode == FindingCodes.ReturnNotPosted);
        Assert.Equal(new RecordRef("direct_debit_events", "D-3"), f.Primary);
    }
}
