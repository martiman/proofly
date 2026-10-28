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
        Assert.Equal("transfer_instructions:" + g1.Left[0].TransactionId.Split(':')[1], g1.Left[0].TransactionId);
        Assert.Equal("REF-1", g1.Left[0].Reference);
        Assert.Equal("payments row-1", g1.Left[0].Label);
        Assert.Equal(new MatchMoney(100.50m, "GBP"), g1.Left[0].Amount);
        Assert.Single(g1.Right);
        Assert.Equal("REF-1", g1.Right[0].Reference);
        Assert.Equal(new MatchMoney(0m, "GBP"), g1.Difference);

        var g2 = groups[1];
        Assert.Null(g2.Difference);
        Assert.Equal("unspecified", g2.RuleVersion);
        Assert.Equal("0", g2.Tolerance);
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
        Assert.Equal("manual", g4.RuleVersion);
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
        Assert.StartsWith("transfer_instructions:", focus);
        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-EMPTY", null, null, default)).Failure);
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

        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-M1", null, "EX-X", default)).Failure);
        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-M1", null, "EX-nope", default)).Failure);
        Assert.Equal(FocusFailure.NotFound, (await Repo.ResolveFocusAsync("R-M2", bank, null, default)).Failure);
        Assert.Equal(FocusFailure.InvalidTransactionId, (await Repo.ResolveFocusAsync("R-M1", "garbage", null, default)).Failure);
        Assert.Equal(FocusFailure.InvalidTransactionId, (await Repo.ResolveFocusAsync("R-M1", "users:1", null, default)).Failure);
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
