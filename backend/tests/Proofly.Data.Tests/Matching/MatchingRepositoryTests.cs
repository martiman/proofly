using Proofly.Data.Matching;

namespace Proofly.Data.Tests.Matching;

public class MatchingRepositoryTests(SchemaFixture f) : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;
    private MatchingRepository Repo => new(f.DataSource);

    public async Task InitializeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_seeded) { return; }
            await new MigrationRunner(f.DataSource).RunAsync();
            await f.Exec(MatchingSeed.Sql);
            _seeded = true;
        }
        finally { Gate.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Run_scope_returns_entity_or_null()
    {
        Assert.Equal("LE-M2", (await Repo.GetRunScopeAsync("R-M2", default))!.LegalEntityId);
        Assert.Null(await Repo.GetRunScopeAsync("nope", default));
    }

    [Fact]
    public async Task Lists_groups_with_sides_rule_tolerance_exceptions_and_approval()
    {
        var groups = await Repo.ListGroupsAsync("R-M1", null, 10, null, default);
        Assert.Equal(["G-001", "G-002", "G-003", "G-004", "G-005"], groups.Select(g => g.Id));

        var g1 = groups[0];
        Assert.Equal("rule-ref@2.1", g1.RuleVersion);
        Assert.Equal("0.01 GBP", g1.Tolerance);
        Assert.Equal("1:1", g1.Cardinality);
        Assert.Matches(@"^TRF-\d+$", g1.Left[0].TransactionId);
        Assert.Matches(@"^BNK-\d+$", g1.Right[0].TransactionId);
        Assert.Equal(("LEFT", "RIGHT"), (g1.Left[0].Side, g1.Right[0].Side)); // derived from kind: the seed stores no side
        Assert.Equal(1m, g1.Left[0].Score);
        Assert.True(g1.Left[0].Selected);
        Assert.Equal("Reference REF-1 equal on both sides", g1.Rationale);
        Assert.Equal("REF-1", g1.Left[0].Reference);
        Assert.Equal("payments row-1", g1.Left[0].Label);
        Assert.Equal(new MatchMoney(100.50m, "GBP"), g1.Left[0].Amount);
        Assert.Single(g1.Right);
        Assert.Equal("REF-1", g1.Right[0].Reference);
        Assert.Equal(new MatchMoney(0m, "GBP"), g1.Difference);

        var g2 = groups[1];
        Assert.Null(g2.Difference);
        Assert.Null(g2.RuleVersion);
        Assert.Null(g2.Tolerance);
        Assert.Null(g2.Rationale);
        Assert.Equal("1:0", g2.Cardinality);
        Assert.Equal(["EX-1"], g2.ExceptionIds);
        Assert.Equal("I-2", g2.Left[0].Reference); // external_reference null falls back to instruction id

        var g3 = groups[2];
        Assert.Equal("rule-ref@2.1", g3.RuleVersion);
        Assert.Equal(new MatchMoney(2.5m, "JPY"), g3.Difference);
        Assert.Equal(["EX-3"], g3.ExceptionIds);
        Assert.Equal(49997.5m, g3.Right[0].Amount.Amount);

        var g4 = groups[3];
        Assert.True(g4.Manual);
        Assert.Null(g4.RuleVersion); // manual match, no rule: nothing invented
        Assert.Equal("L-4", g4.Right[0].Label.Split(' ')[1]);
        Assert.Equal("AP-1", g4.Approval!.Id);
        Assert.Equal("Pat Proposer", g4.Approval.ProposerName);
        Assert.Null(g4.Approval.ApproverId);
        Assert.Equal(["ev-1"], g4.Approval.EvidenceRefs);
        Assert.Null(groups[0].Approval);
    }

    [Fact]
    public async Task Filters_by_status_and_pages_by_keyset()
    {
        var unmatched = await Repo.ListGroupsAsync("R-M1", "UNMATCHED", 10, null, default);
        Assert.Equal(["G-002", "G-005"], unmatched.Select(g => g.Id));

        var page1 = await Repo.ListGroupsAsync("R-M1", null, 2, null, default);
        Assert.Equal(3, page1.Count); // limit + 1 signals more
        var page2 = await Repo.ListGroupsAsync("R-M1", null, 2, "G-002", default);
        Assert.Equal(["G-003", "G-004", "G-005"], page2.Select(g => g.Id));
        Assert.Equal(2, await Repo.CountGroupsAsync("R-M1", "UNMATCHED", default));
        Assert.Equal(5, await Repo.CountGroupsAsync("R-M1", null, default));
    }

    [Fact]
    public async Task Groups_are_bound_to_the_run()
    {
        Assert.Equal(["G-006"], (await Repo.ListGroupsAsync("R-M2", null, 10, null, default)).Select(g => g.Id));
        Assert.Empty(await Repo.ListGroupsAsync("R-EMPTY", null, 10, null, default));
        Assert.Null(await Repo.GetGroupAsync("R-M2", "G-001", default));
    }

    [Fact]
    public async Task Focus_defaults_to_first_open_group()
    {
        var (g, focus, fail) = await Repo.ResolveFocusAsync("R-M1", null, null, default);
        Assert.Equal(FocusFailure.None, fail);
        Assert.Equal("G-002", g); // G-001 is matched
        Assert.StartsWith("TRF-", focus);
        Assert.Equal((null, null, FocusFailure.None), await Repo.ResolveFocusAsync("R-EMPTY", null, null, default)); // an empty run is not an error
    }

    [Fact]
    public async Task Focus_by_transaction_and_by_exception()
    {
        var g3 = (await Repo.GetGroupAsync("R-M1", "G-003", default))!;
        var bank = g3.Right[0].TransactionId;
        var byTx = await Repo.ResolveFocusAsync("R-M1", bank, null, default);
        Assert.Equal(("G-003", bank, FocusFailure.None), byTx);

        var byEx = await Repo.ResolveFocusAsync("R-M1", null, "EX-3", default);
        Assert.Equal(("G-003", bank, FocusFailure.None), byEx);

        // EX-X exists but sits on no candidate (only on a group through match_group_exceptions): group resolved, first candidate focused.
        var byGroupLink = await Repo.ResolveFocusAsync("R-M1", null, "EX-X", default);
        Assert.Equal(("G-005", FocusFailure.None), (byGroupLink.GroupId, byGroupLink.Failure));
        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-M1", null, "EX-nope", default)).Failure);
        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-M2", bank, null, default)).Failure); // record is not in that run
        // A transaction that exists in the run but is in no group: no group, the transaction stays the focus.
        await using var q = f.DataSource.CreateCommand("SELECT id FROM bank_transactions WHERE external_transaction_id = 'B-9'");
        var b9 = "BNK-" + (long)(await q.ExecuteScalarAsync())!;
        Assert.Equal((null, b9, FocusFailure.None), await Repo.ResolveFocusAsync("R-M1", b9, null, default));
        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-M1", "BNK-999999", null, default)).Failure);
        Assert.Equal(FocusFailure.InvalidTransactionId, (await Repo.ResolveFocusAsync("R-M1", "garbage", null, default)).Failure);
        Assert.Equal(FocusFailure.InvalidTransactionId, (await Repo.ResolveFocusAsync("R-M1", "users:1", null, default)).Failure);
        Assert.Equal(FocusFailure.InvalidTransactionId, (await Repo.ResolveFocusAsync("R-M1", "XYZ-1", null, default)).Failure);
    }

    [Fact]
    public async Task Unmatched_population_and_stats_are_per_currency_decimal()
    {
        var unmatched = await Repo.ListUnmatchedAsync("R-M1", 200, default);
        Assert.Equal([250m, 10.25m], unmatched.Select(u => u.Amount.Amount));

        var s = await Repo.GetStatsAsync("R-M1", default);
        Assert.Equal(5, s.TotalGroups);
        Assert.Equal(4, s.OpenGroups);
        Assert.Equal(0.2m, s.AutoMatchRate);
        Assert.Equal(2, s.UnmatchedCount);
        Assert.Equal([new CurrencyUnmatched("EUR", 1, 250m), new CurrencyUnmatched("GBP", 1, 10.25m)], s.UnmatchedByCurrency);

        var empty = await Repo.GetStatsAsync("R-EMPTY", default);
        Assert.Null(empty.AutoMatchRate);
        Assert.Empty(empty.UnmatchedByCurrency);
    }
}
