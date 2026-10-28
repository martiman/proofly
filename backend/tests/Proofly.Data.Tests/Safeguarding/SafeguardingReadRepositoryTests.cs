using Proofly.Data.Safeguarding;

namespace Proofly.Data.Tests.Safeguarding;

public sealed class SafeguardingFixture : IAsyncLifetime
{
    private readonly SchemaFixture _schema = new();
    public Npgsql.NpgsqlDataSource Db => _schema.DataSource;
    public Task<T?> Scalar<T>(string sql) => _schema.Scalar<T>(sql);
    public Task Exec(string sql) => _schema.Exec(sql);

    public async Task InitializeAsync()
    {
        await _schema.InitializeAsync();
        await new MigrationRunner(Db).RunAsync();
        await SafeguardingSeed.ApplyAsync(Db);
    }

    public Task DisposeAsync() => _schema.DisposeAsync();
}

public sealed class SafeguardingReadRepositoryTests : IClassFixture<SafeguardingFixture>
{
    private readonly SafeguardingFixture _f;
    private readonly SafeguardingReadRepository _repo;
    private static readonly EntityScope Le1 = EntityScope.Only("LE-1");

    public SafeguardingReadRepositoryTests(SafeguardingFixture f)
    {
        _f = f;
        _repo = new SafeguardingReadRepository(f.Db);
    }

    [Fact]
    public async Task Positions_are_per_currency_and_never_netted()
    {
        var v = (await _repo.GetPositionAsync("R-1", Le1, default))!;
        Assert.Equal(["EUR", "GBP", "JPY"], v.Positions.Select(p => p.Currency));
        var gbp = v.Positions.Single(p => p.Currency == "GBP");
        Assert.Equal((1000.25m, 900.5m, 0m, -99.75m, "SHORTFALL"), (gbp.Obligation, gbp.Eligible, gbp.ApprovedAdjustments, gbp.Result, gbp.State));
        Assert.Equal(["EX-1", "EX-2"], gbp.ExceptionIds);
        Assert.Equal(["STALE_INPUT"], v.Positions.Single(p => p.Currency == "EUR").Qualifications);
        Assert.Equal("NO_OBLIGATION", v.Positions.Single(p => p.Currency == "JPY").State);
        Assert.Equal("APPROVAL_PENDING", v.Run.SignOffState);
    }

    [Fact]
    public async Task Approved_adjustments_fall_back_to_live_sum_when_snapshot_has_none()
    {
        var v = (await _repo.GetPositionAsync("R-1", Le1, default))!;
        // JPY detail is empty: live sum covers APPROVED+unexpired only (the JPY adjustment is rejected)
        Assert.Equal(0m, v.Positions.Single(p => p.Currency == "JPY").ApprovedAdjustments);
    }

    [Fact]
    public async Task Obligation_and_balance_lines_carry_reasons_and_evidence()
    {
        var v = (await _repo.GetPositionAsync("R-1", Le1, default))!;
        Assert.Equal(["CA-3", "CA-1", "CA-2"], v.Obligations.Select(o => o.AccountId));
        Assert.Equal("CLIENT_FUNDS", v.Obligations.Single(o => o.AccountId == "CA-3").MovementType);
        var r1 = v.Balances.Single(b => b.SourceRowId == "row-1");
        Assert.True(r1.Included);
        Assert.Equal((900.5m, "stmt-1", "ELIGIBLE"), (r1.Balance, r1.EvidenceReference, r1.Reason));
        var r2 = v.Balances.Single(b => b.SourceRowId == "row-2");
        Assert.False(r2.Included);
        Assert.Equal(["F-1"], r2.FindingIds);
        var r5 = v.Balances.Single(b => b.SourceRowId == "row-5");
        Assert.False(r5.Included);
        Assert.Equal("NOT_EVALUATED", r5.Reason);
    }

    [Fact]
    public async Task Previous_business_date_positions_feed_change_analysis()
    {
        var v = (await _repo.GetPositionAsync("R-1", Le1, default))!;
        Assert.Equal(["EUR", "GBP"], v.Previous.Select(p => p.Currency));
        Assert.Equal(-50m, v.Previous.Single(p => p.Currency == "GBP").Result);
    }

    [Fact]
    public async Task Run_without_positions_is_incomplete()
    {
        var v = (await _repo.GetPositionAsync("R-EMPTY", Le1, default))!;
        Assert.Empty(v.Positions);
        Assert.Equal("INCOMPLETE", v.EvidenceReadiness);
        Assert.Equal("QUALIFIED", (await _repo.GetPositionAsync("R-1", Le1, default))!.EvidenceReadiness);
    }

    [Fact]
    public async Task Out_of_scope_and_unknown_runs_return_null()
    {
        Assert.Null(await _repo.GetPositionAsync("R-2", Le1, default));
        Assert.Null(await _repo.GetRunAsync("R-2", Le1, default));
        Assert.Null(await _repo.ListAdjustmentsAsync("R-2", null, Le1, default));
        Assert.Null(await _repo.GetPositionAsync("nope", EntityScope.Everything, default));
        Assert.NotNull(await _repo.GetPositionAsync("R-2", EntityScope.Everything, default));
        Assert.Null(await _repo.GetPositionAsync("R-1", EntityScope.Only(), default));
    }

    [Fact]
    public async Task Adjustments_are_ordered_filterable_and_carry_approval_and_history()
    {
        var all = (await _repo.ListAdjustmentsAsync("R-1", null, Le1, default))!;
        Assert.Equal(["ADJ-1", "ADJ-2", "ADJ-3"], all.Select(a => a.Id));
        var a1 = all[0];
        Assert.Equal((25.5m, "Ann", new DateOnly(2026, 11, 30)), (a1.Amount, a1.Owner.Name, a1.ExpiresOn));
        Assert.Equal("Bob", a1.Approval!.Approver!.Name);
        Assert.Equal(["ev-1", "ev-2"], a1.Approval.EvidenceRefs);
        Assert.Equal(["ADJUSTMENT_PROPOSED", "ADJUSTMENT_APPROVED"], a1.History.Select(h => h.EventType));
        Assert.Null(all[2].Approval);

        var proposed = (await _repo.ListAdjustmentsAsync("R-1", "PROPOSED", Le1, default))!;
        Assert.Equal(["ADJ-2"], proposed.Select(a => a.Id));
        Assert.Empty((await _repo.ListAdjustmentsAsync("R-1", "EXPIRED", Le1, default))!);
    }

    [Fact]
    public async Task Live_approved_sum_used_in_position_when_snapshot_lacks_it()
    {
        await _f.Exec("INSERT INTO safeguarding_adjustments (id,run_id,currency,amount,reason,status,proposed_by_user_id) VALUES ('ADJ-L','R-2','EUR',4.5,'live','APPROVED','u-ann')");
        var v = (await _repo.GetPositionAsync("R-2", EntityScope.Everything, default))!;
        Assert.Equal(4.5m, v.Positions.Single().ApprovedAdjustments);
    }
}
