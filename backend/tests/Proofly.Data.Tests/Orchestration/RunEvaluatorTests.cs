using Proofly.Controls.Completeness;
using Proofly.Data.Orchestration;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Orchestration;

/// <summary>The evaluator is pure: no database, no clock. Neutral GBP/JPY records built through the canonical types.</summary>
public class RunEvaluatorTests
{
    private static readonly DateOnly Date = new(2026, 10, 27);
    private static readonly DateTimeOffset Ts = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private static List<LoadedRecord> Records(decimal sgGbp, long idOffset)
    {
        var list = new List<LoadedRecord>();
        long id = idOffset;
        void Add(ISourceRecord r, string table, string source) => list.Add(new LoadedRecord(table, id++, "D-1", source, r, null));
        Add(new LedgerEntry("L-1", "A1", "LE-1", Ts, Date, DebitCredit.Debit, new Money(100m, "GBP"), "TRF-1", LedgerEntryType.Transfer, LedgerEntryStatus.Posted), "ledger_entries", SourceNames.InternalLedgerEntries);
        Add(new LedgerEntry("L-2", "A2", "LE-1", Ts, Date, DebitCredit.Debit, new Money(5000m, "JPY"), "TRF-2", LedgerEntryType.Transfer, LedgerEntryStatus.Posted), "ledger_entries", SourceNames.InternalLedgerEntries);
        Add(new AccountBalance("A1", "LE-1", Date, AccountBalanceType.DailyRollforward, new Money(1000m, "GBP"), new Money(900m, "GBP"), Ts), "account_balances", SourceNames.ClientAccountBalances);
        Add(new AccountBalance("A2", "LE-1", Date, AccountBalanceType.DailyRollforward, new Money(20000m, "JPY"), new Money(15000m, "JPY"), Ts), "account_balances", SourceNames.ClientAccountBalances);
        Add(new SafeguardingBalance("SG-1", "SGA-1", "LE-1", Ts, SafeguardingBalanceType.ClosingAvailable, new Money(sgGbp, "GBP"), "STMT-1", Ts), "safeguarding_balances", SourceNames.SafeguardingBalances);
        Add(new SafeguardingBalance("SG-2", "SGA-2", "LE-1", Ts, SafeguardingBalanceType.ClosingAvailable, new Money(15000m, "JPY"), "STMT-2", Ts), "safeguarding_balances", SourceNames.SafeguardingBalances);
        return list;
    }

    private static RunInputs Inputs(string runId, IReadOnlyList<LoadedRecord> records) =>
        new(runId, "LE-1", Date, [], Array.Empty<EvidenceDelivery>(), Array.Empty<RejectedRecord>(), records, []);

    [Fact]
    public void Hash_is_independent_of_run_id_database_ids_and_record_order()
    {
        var o = new RunProcessingOptions();
        var a = RunEvaluator.Evaluate(Inputs("RUN-A", Records(800m, 1)), o);
        var b = RunEvaluator.Evaluate(Inputs("RUN-B", Records(800m, 500).AsEnumerable().Reverse().ToList()), o);
        Assert.Equal(a.ResultHash, b.ResultHash);
        Assert.Matches("^[0-9a-f]{64}$", a.ResultHash);
        Assert.NotEqual(a.Exceptions.Select(e => e.Id), b.Exceptions.Select(e => e.Id)); // ids are per run, findings are not
        Assert.Equal(a.Exceptions.Select(e => e.Finding.Key), b.Exceptions.Select(e => e.Finding.Key));
        Assert.Equal(a.Exceptions.Select(e => e.Id), RunEvaluator.Evaluate(Inputs("RUN-A", Records(800m, 99)), o).Exceptions.Select(e => e.Id)); // stable within a run
    }

    [Fact]
    public void Changed_evidence_or_ruleset_changes_the_hash_and_money_stays_decimal()
    {
        var o = new RunProcessingOptions();
        var baseline = RunEvaluator.Evaluate(Inputs("R", Records(800m, 1)), o);
        Assert.NotEqual(baseline.ResultHash, RunEvaluator.Evaluate(Inputs("R", Records(700m, 1)), o).ResultHash);
        var other = o with { Safeguarding = o.Safeguarding with { RuleVersion = "safeguarding-2.0" } };
        Assert.NotEqual(baseline.ResultHash, RunEvaluator.Evaluate(Inputs("R", Records(800m, 1)), other).ResultHash);
        var shortfall = baseline.Exceptions.Single(e => e.Finding.ReasonCode == "SHORTFALL");
        Assert.Equal(new Money(-100m, "GBP"), shortfall.Finding.Value);
        Assert.True(shortfall.Finding.Blocking);
        Assert.Equal("safeguarding.SHORTFALL", shortfall.RuleId);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), shortfall.DueAt); // critical: one day
        Assert.Contains(baseline.Positions, p => p.Position.Currency == "JPY" && p.Position.State == SafeguardingState.Covered);
    }
}
