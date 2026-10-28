using Proofly.Data.Exceptions;

namespace Proofly.Data.Tests.Exceptions;

public class ExceptionReadRepositoryTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    private ExceptionReadRepository Repo => new(_f.DataSource);
    public ExceptionReadRepositoryTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync()
    {
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("TRUNCATE legal_entities, users CASCADE"); // fresh data per test; the fixture schema is shared
        await _f.Exec(ExceptionSeed.Sql);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private static ExceptionSortSpec Sort(string? s = null)
    {
        Assert.True(ExceptionSortSpec.TryParse(s, out var spec, out var err), err);
        return spec;
    }

    private async Task<string[]> Ids(ExceptionFilter? f = null, string? sort = null, string run = "R1", IReadOnlyCollection<string>? scope = null)
    {
        var r = await Repo.ListAsync(new ExceptionListQuery(run, scope, f ?? new ExceptionFilter(), Sort(sort), 100, null, false, ExceptionSeed.Now));
        return r.Rows.Select(x => x.Id).ToArray();
    }

    [Fact]
    public async Task Default_sort_is_severity_desc_then_due_then_id()
    {
        Assert.Equal(["E-001", "E-006", "E-003", "E-002", "E-004", "E-007", "E-005"], await Ids());
    }

    [Fact]
    public async Task Other_runs_and_scopes_are_excluded()
    {
        Assert.Equal(["X-001"], await Ids(run: "R2"));
        Assert.Empty(await Ids(scope: ["LE-2"])); // R1 belongs to LE-1
        Assert.Equal(7, (await Ids(scope: ["LE-1", "LE-2"])).Length);
        Assert.Empty(await Ids(scope: []));
    }

    [Fact]
    public async Task Filters_by_severity_status_control_owner_blocking()
    {
        Assert.Equal(["E-001", "E-006"], await Ids(new ExceptionFilter { Severity = ["CRITICAL"] }));
        Assert.Equal(["E-003"], await Ids(new ExceptionFilter { Status = ["IN_INVESTIGATION"] }));
        Assert.Equal(["E-005"], await Ids(new ExceptionFilter { Control = ["data_completeness"] }));
        Assert.Equal(["E-001", "E-005"], await Ids(new ExceptionFilter { OwnerId = "u1" }, "id"));
        Assert.Equal(["E-002", "E-004", "E-006", "E-007"], await Ids(new ExceptionFilter { Unassigned = true }, "id"));
        Assert.Equal(["E-001", "E-006"], await Ids(new ExceptionFilter { Blocking = true }, "id"));
        Assert.Equal(["E-001", "E-006"], await Ids(new ExceptionFilter { Severity = ["CRITICAL", "LOW"], Status = ["OPEN"] }, "id"));
    }

    [Fact]
    public async Task Value_range_is_absolute_and_per_currency()
    {
        Assert.Equal(["E-001", "E-004", "E-007"], await Ids(new ExceptionFilter { Currency = "GBP" }, "id"));
        Assert.Equal(["E-001"], await Ids(new ExceptionFilter { Currency = "GBP", ValueMin = 50, ValueMax = 200 }));
        Assert.Equal(["E-002", "E-006"], await Ids(new ExceptionFilter { Currency = "EUR", ValueMin = 1500 }, "id"));
        Assert.Equal(["E-003"], await Ids(new ExceptionFilter { Currency = "JPY", ValueMax = 100000 }));
    }

    [Fact]
    public async Task Filters_by_business_date_age_due_and_search()
    {
        Assert.Equal(["E-003", "E-005"], await Ids(new ExceptionFilter { BusinessDateTo = new DateOnly(2026, 10, 26) }, "id"));
        Assert.Equal(["E-001", "E-004", "E-006", "E-007"], await Ids(new ExceptionFilter { BusinessDateFrom = new DateOnly(2026, 10, 27), Status = ["OPEN", "RESOLVED"], Control = ["matching"] }, "id"));
        // age at 2026-10-28T12:00Z: E-003 created 10-20 -> 8 days
        Assert.Equal(["E-003"], await Ids(new ExceptionFilter { AgeDaysMin = 8 }));
        Assert.Equal(["E-002", "E-005"], await Ids(new ExceptionFilter { AgeDaysMin = 2, AgeDaysMax = 3 }, "id"));
        Assert.Equal(["E-001", "E-003", "E-006", "E-007"], await Ids(new ExceptionFilter { DueBefore = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero) }, "id"));
        Assert.Equal(["E-002"], await Ids(new ExceptionFilter { Search = "ledger gap" }));
        Assert.Equal(["E-004"], await Ids(new ExceptionFilter { Search = "row-4" }));
        Assert.Empty(await Ids(new ExceptionFilter { Search = "100%" })); // wildcard is escaped, not interpreted
    }

    [Fact]
    public void Sort_parsing_requires_known_keys()
    {
        Assert.True(ExceptionSortSpec.TryParse("value", out var s, out _));
        Assert.Equal("value,id", s.Normalized);
        Assert.False(ExceptionSortSpec.TryParse("-bogus", out _, out var err));
        Assert.Contains("bogus", err);
        Assert.False(ExceptionSortSpec.TryParse("id,-id", out _, out _));
    }

    [Fact]
    public async Task Sorts_by_value_business_date_and_age()
    {
        Assert.Equal(["E-005", "E-007", "E-004", "E-001", "E-006", "E-002", "E-003"], await Ids(sort: "value")); // no value first (-1)
        Assert.Equal(["E-003", "E-002", "E-006", "E-001", "E-004", "E-007", "E-005"], await Ids(sort: "-value"));
        Assert.Equal("E-003", (await Ids(sort: "-ageDays"))[0]); // oldest first
        Assert.Equal("E-007", (await Ids(sort: "ageDays"))[0]);
        Assert.Equal(["E-005", "E-003"], (await Ids(sort: "businessDate")).Take(2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-value,dueAt")]
    [InlineData("businessDate,-createdAt")]
    [InlineData("-ageDays")]
    [InlineData("status,-control,id")]
    public async Task Keyset_paging_visits_every_row_once_in_the_same_order_as_one_page(string? sort)
    {
        var all = await Ids(sort: sort);
        var seen = new List<string>();
        IReadOnlyList<string>? after = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var r = await Repo.ListAsync(new ExceptionListQuery("R1", null, new ExceptionFilter(), Sort(sort), 2, after, true, ExceptionSeed.Now));
            Assert.Equal(7, r.Total);
            seen.AddRange(r.Rows.Take(2).Select(x => x.Id));
            if (r.Rows.Count <= 2) { break; }
            after = r.Rows[1].SortKeys;
        }
        Assert.Equal(all, seen);
    }

    [Fact]
    public async Task Summary_aggregates_per_currency_and_never_across_currencies()
    {
        var s = await Repo.SummaryAsync("R1", null, new ExceptionFilter(), ExceptionSeed.Now);
        Assert.Equal(7, s.Total);
        Assert.Equal(2, s.Blocking);
        Assert.Equal(1, s.WithoutValue);
        Assert.Equal(3, s.Overdue); // E-001, E-003, E-006; resolved E-007 excluded
        Assert.Equal(3, s.Unassigned); // E-002, E-004, E-006 (E-007 is terminal)
        Assert.Equal(2, s.BySeverity["CRITICAL"]);
        Assert.Equal(5, s.ByControl["matching"]);
        Assert.Equal(["EUR", "GBP", "JPY"], s.ByCurrency.Select(c => c.Currency).ToArray());
        var gbp = s.ByCurrency.Single(c => c.Currency == "GBP");
        Assert.Equal((3, -60.25m, 140.75m), (gbp.Count, gbp.TotalValue, gbp.AbsoluteValue));
        var eur = s.ByCurrency.Single(c => c.Currency == "EUR");
        Assert.Equal((2, 499.25m, 3500.75m), (eur.Count, eur.TotalValue, eur.AbsoluteValue));
        Assert.Equal(50000m, s.ByCurrency.Single(c => c.Currency == "JPY").TotalValue);
    }

    [Fact]
    public async Task Summary_honours_filters_and_scope()
    {
        var s = await Repo.SummaryAsync("R1", null, new ExceptionFilter { Currency = "GBP", Status = ["OPEN"] }, ExceptionSeed.Now);
        Assert.Equal(2, s.Total);
        Assert.Equal(-70.25m, s.ByCurrency.Single().TotalValue);
        var none = await Repo.SummaryAsync("R1", ["LE-2"], new ExceptionFilter(), ExceptionSeed.Now);
        Assert.Equal(0, none.Total);
        Assert.Empty(none.ByCurrency);
    }

    [Fact]
    public async Task Detail_joins_evidence_timeline_approvals_and_respects_scope()
    {
        await _f.Exec("""
            INSERT INTO exception_evidence_refs (exception_id, kind, ref) VALUES ('E-001','BANK_TRANSACTION','bt-1'),('E-001','account','acc-9');
            INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary, occurred_at)
              VALUES ('E-001','ASSIGNMENT','USER','u1','Assigned to Ada','2026-10-27T09:00Z'),('E-001','SYSTEM','SYSTEM',NULL,'Raised','2026-10-27T08:00Z');
            INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id) VALUES ('P-1','E-001','RESOLVED','because','u2');
            INSERT INTO approvals (id, object_type, object_id, proposal_version, proposer_user_id, required_role, policy_version)
              VALUES ('A-1','EXCEPTION_RESOLUTION','P-1',1,'u2','APPROVER','pol-3');
            """);
        var d = await Repo.GetDetailAsync("E-001", null, ExceptionSeed.Now);
        Assert.NotNull(d);
        Assert.Equal("rs-1", d.RulesetVersion);
        Assert.Equal(2, d.Evidence.Count);
        Assert.Equal(["Raised", "Assigned to Ada"], d.Timeline.Select(t => t.Summary).ToArray());
        Assert.Equal("Ada", d.Timeline[1].ActorName);
        Assert.Equal("A-1", Assert.Single(d.Approvals).Id);
        Assert.Equal("Bob", d.Approvals[0].ProposerName);
        Assert.Equal(1, d.Row.Version);
        Assert.Null(await Repo.GetDetailAsync("E-001", ["LE-2"], ExceptionSeed.Now));
        Assert.Null(await Repo.GetDetailAsync("nope", null, ExceptionSeed.Now));
    }

    [Fact]
    public async Task Version_column_increments_on_update()
    {
        await _f.Exec("UPDATE exceptions SET title = 'Changed' WHERE id = 'E-004'");
        Assert.Equal(2, (await Repo.GetDetailAsync("E-004", null, ExceptionSeed.Now))!.Row.Version);
    }

    [Fact]
    public async Task Run_lookup_and_saved_view_visibility()
    {
        Assert.Equal("LE-2", await Repo.FindRunLegalEntityAsync("R2"));
        Assert.Null(await Repo.FindRunLegalEntityAsync("nope"));
        await _f.Exec("""
            INSERT INTO saved_views (id, user_id, scope, name, filters, shared) VALUES
              ('V-1','u1','exceptions','mine','{"severity":["HIGH"]}',false),('V-2','u2','exceptions','shared','{"blocking":true}',true),
              ('V-3','u2','transactions','other scope','{}',true);
            """);
        Assert.Contains("HIGH", await Repo.FindSavedViewFiltersAsync("V-1", "u1") ?? "");
        Assert.Null(await Repo.FindSavedViewFiltersAsync("V-1", "u2")); // personal view of someone else
        Assert.NotNull(await Repo.FindSavedViewFiltersAsync("V-2", "u1")); // shared
        Assert.Null(await Repo.FindSavedViewFiltersAsync("V-3", "u1")); // other scope
    }
}
