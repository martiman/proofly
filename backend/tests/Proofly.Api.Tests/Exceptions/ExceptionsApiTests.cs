using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Exceptions;

public sealed class ExceptionsFixture : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal WebApplicationFactory<Program> Factory { get; private set; } = null!;
    internal LocalTokenService Tokens => Factory.Services.GetRequiredService<LocalTokenService>();

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={Schema}");
        await new MigrationRunner(Db).RunAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Proofly", $"{Cs};Search Path={Schema}");
            b.UseSetting("Auth:Mode", "Local");
        });
        _ = Factory.Server; // seeds the local users
        await Exec(ExceptionsSeed.Sql);
    }

    public async Task Exec(string sql)
    {
        await using var c = Db.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    public HttpClient As(string? token)
    {
        var c = Factory.CreateClient();
        if (token is not null) { c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        return c;
    }

    public string TokenFor(Role role) => Tokens.Issue(LocalUsers.Subject(role));

    public async Task<string> ScopedToken(string key, Role role, string legalEntity)
    {
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true,
            [new RoleAssignment(WireNames.ToUpperSnake(role), legalEntity)]));
        return Tokens.Issue("sub-" + key);
    }
}

internal static class ExceptionsSeed
{
    public const string Sql = """
        INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-1','One','GBP'),('LE-2','Two','EUR');
        INSERT INTO users (id, name, email) VALUES ('u1','Ada','ada@x.test'),('u2','Bob','bob@x.test');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state)
          VALUES ('R1','LE-1','2026-10-27',1,'rs-1','REVIEW_REQUIRED'),('R2','LE-2','2026-10-27',1,'rs-1','REVIEW_REQUIRED'),
                 ('R3','LE-1','2026-10-26',1,'rs-1','SUPERSEDED');
        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status,
                                value_amount, value_currency, owner_user_id, due_at, blocking, source_file, source_row_id, created_at) VALUES
          ('E-001','R1','LE-1','2026-10-27','Transfer without bank evidence','matching','NO_BANK_EVIDENCE','CRITICAL','OPEN',-100.50,'GBP','u1','2026-10-27T09:00Z',true,'transfers.csv','row-1','2026-10-27T08:00Z'),
          ('E-002','R1','LE-1','2026-10-27','Ledger gap','independent_ledger','LEDGER_GAP','HIGH','OPEN',2000,'EUR',NULL,'2026-10-29T09:00Z',false,'ledger.csv','row-2','2026-10-26T08:00Z'),
          ('E-003','R1','LE-1','2026-10-26','Card settlement short','matching','SETTLEMENT_SHORT','HIGH','IN_INVESTIGATION',50000,'JPY','u2','2026-10-28T09:00Z',false,'cards.csv','row-3','2026-10-20T08:00Z'),
          ('E-004','R1','LE-1','2026-10-27','Small mismatch','matching','NO_BANK_EVIDENCE','MEDIUM','OPEN',30.25,'GBP',NULL,'2026-11-05T09:00Z',false,'transfers.csv','row-4','2026-10-27T10:00Z'),
          ('E-005','R1','LE-1','2026-10-25','Late delivery','data_completeness','LATE_DELIVERY','LOW','WAITING_FOR_DATA',NULL,NULL,'u1','2026-10-30T09:00Z',false,NULL,NULL,'2026-10-25T08:00Z'),
          ('E-006','R1','LE-1','2026-10-27','Eur mismatch','matching','NO_BANK_EVIDENCE','CRITICAL','OPEN',-1500.75,'EUR',NULL,'2026-10-28T09:00Z',true,'transfers.csv','row-6','2026-10-27T11:00Z'),
          ('E-007','R1','LE-1','2026-10-27','Closed item','matching','NO_BANK_EVIDENCE','LOW','RESOLVED',10,'GBP',NULL,'2026-10-20T09:00Z',false,'transfers.csv','row-7','2026-10-27T12:00Z'),
          ('X-001','R2','LE-2','2026-10-27','Other entity','matching','NO_BANK_EVIDENCE','HIGH','OPEN',5,'EUR',NULL,'2026-10-29T09:00Z',false,'x.csv','row-1','2026-10-27T08:00Z'),
          ('S-001','R3','LE-1','2026-10-26','Old run exception','matching','NO_BANK_EVIDENCE','HIGH','OPEN',1,'GBP',NULL,'2026-10-29T09:00Z',false,'x.csv','row-9','2026-10-26T08:00Z');
        INSERT INTO exception_evidence_refs (exception_id, kind, ref) VALUES ('E-001','BANK_TRANSACTION','bt-1'),('E-001','ACCOUNT','acc-9');
        INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary, occurred_at)
          VALUES ('E-001','ASSIGNMENT','USER','u1','Assigned to Ada','2026-10-27T09:00Z'),('E-001','SYSTEM','SYSTEM',NULL,'Raised','2026-10-27T08:00Z');
        INSERT INTO exception_rule_traces (exception_id, rule_id, rule_version, tolerance, expected_effect, description, calculation) VALUES
          ('E-004','NO_BANK_EVIDENCE','matching/2','0.01 GBP','Transfer is not backed by a bank booking','No bank booking within the window',
           '{"inputs":[{"name":"transferAmount","value":"30.25 GBP"},{"name":"bankAmount","value":"none"}],"text":"bank - transfer","result":"-30.25 GBP"}');
        INSERT INTO saved_views (id, user_id, scope, name, filters, shared) VALUES ('V-1','u1','exceptions','blocking','{"blocking":true,"severity":["CRITICAL"]}',true);
        """;
}

public class ExceptionsApiTests(ExceptionsFixture f) : IClassFixture<ExceptionsFixture>
{
    private const string Base = "/api/v1/runs/R1/exceptions";
    private HttpClient Viewer => f.As(f.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task List_returns_the_contract_page_with_default_sort_and_money_strings()
    {
        var res = await Viewer.GetAsync(Base);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var page = await Json(res);
        Assert.Equal(["E-001", "E-006", "E-003", "E-002", "E-004", "E-007", "E-005"], Ids(page));
        var first = page.GetProperty("items")[0];
        Assert.Equal("-100.500000", first.GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("GBP", first.GetProperty("value").GetProperty("currency").GetString());
        Assert.Equal("Ada", first.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal("\"v1\"", first.GetProperty("etag").GetString());
        Assert.False(page.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("page").GetProperty("nextCursor").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("items")[6].GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task List_pages_with_cursor_and_total()
    {
        var seen = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 10; i++)
        {
            var url = Base + "?limit=3&includeTotal=true" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await Json(await Viewer.GetAsync(url));
            Assert.Equal(7, page.GetProperty("page").GetProperty("total").GetInt32());
            seen.AddRange(Ids(page));
            if (!page.GetProperty("page").GetProperty("hasMore").GetBoolean()) { break; }
            cursor = page.GetProperty("page").GetProperty("nextCursor").GetString();
        }
        Assert.Equal(["E-001", "E-006", "E-003", "E-002", "E-004", "E-007", "E-005"], seen);
    }

    [Fact]
    public async Task List_filters_and_sorts()
    {
        Assert.Equal(["E-001", "E-006"], Ids(await Json(await Viewer.GetAsync(Base + "?severity=CRITICAL,LOW&status=OPEN"))));
        Assert.Equal(["E-002", "E-006"], Ids(await Json(await Viewer.GetAsync(Base + "?currency=EUR&valueMin=1500&sort=id"))));
        Assert.Equal(["E-004", "E-007"], Ids(await Json(await Viewer.GetAsync(Base + "?unassigned=true&control=matching,data_completeness&severity=MEDIUM,LOW"))));
        Assert.Equal(["E-003", "E-002", "E-006", "E-001", "E-004", "E-007", "E-005"], Ids(await Json(await Viewer.GetAsync(Base + "?sort=-value"))));
        Assert.Equal(["E-002"], Ids(await Json(await Viewer.GetAsync(Base + "?search=ledger"))));
        Assert.Empty(Ids(await Json(await Viewer.GetAsync(Base + "?severity=LOW&currency=JPY"))));
    }

    [Fact]
    public async Task Saved_view_supplies_defaults_and_explicit_params_override()
    {
        Assert.Equal(["E-001", "E-006"], Ids(await Json(await Viewer.GetAsync(Base + "?savedViewId=V-1"))));
        Assert.Equal(["E-004"], Ids(await Json(await Viewer.GetAsync(Base + "?savedViewId=V-1&severity=MEDIUM&blocking=false"))));
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync(Base + "?savedViewId=nope")).StatusCode);
    }

    [Theory]
    [InlineData("?sort=bogus")]
    [InlineData("?valueMin=10")]
    [InlineData("?cursor=garbage")]
    [InlineData("?severity=NOPE")]
    [InlineData("?limit=0")]
    public async Task Invalid_input_is_rejected_with_422(string query)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync(Base + query)).StatusCode);
    }

    [Fact]
    public async Task Cursor_from_another_sort_is_rejected()
    {
        var page = await Json(await Viewer.GetAsync(Base + "?limit=2"));
        var cursor = Uri.EscapeDataString(page.GetProperty("page").GetProperty("nextCursor").GetString()!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync(Base + "?limit=2&sort=value&cursor=" + cursor)).StatusCode);
    }

    [Fact]
    public async Task Auth_scope_and_permission_outcomes()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.As(null).GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(f.TokenFor(Role.ConfigAdmin)).GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/nope/exceptions")).StatusCode);
        var le2 = f.As(await f.ScopedToken("le2", Role.Analyst, "LE-2"));
        Assert.Equal(HttpStatusCode.NotFound, (await le2.GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await le2.GetAsync(Base + "/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await le2.GetAsync("/api/v1/exceptions/E-001")).StatusCode);
        Assert.Equal(["X-001"], Ids(await Json(await le2.GetAsync("/api/v1/runs/R2/exceptions"))));
    }

    [Fact]
    public async Task Empty_result_is_a_valid_page()
    {
        var page = await Json(await Viewer.GetAsync("/api/v1/runs/R2/exceptions?severity=LOW"));
        Assert.Empty(Ids(page));
        Assert.False(page.GetProperty("page").GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Summary_is_per_currency_and_server_side()
    {
        var res = await Viewer.GetAsync(Base + "/summary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var s = await Json(res);
        Assert.Equal(7, s.GetProperty("total").GetInt32());
        Assert.Equal(2, s.GetProperty("blocking").GetInt32());
        Assert.Equal(3, s.GetProperty("unassigned").GetInt32());
        Assert.Equal(1, s.GetProperty("withoutValue").GetInt32());
        Assert.Equal(2, s.GetProperty("bySeverity").GetProperty("CRITICAL").GetInt32());
        Assert.Equal(0, s.GetProperty("byStatus").GetProperty("FALSE_POSITIVE").GetInt32());
        var cur = s.GetProperty("byCurrency").EnumerateArray().ToDictionary(c => c.GetProperty("currency").GetString()!);
        Assert.Equal(["EUR", "GBP", "JPY"], cur.Keys.Order().ToArray());
        Assert.Equal(-60.25m, decimal.Parse(cur["GBP"].GetProperty("totalValue").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("GBP", cur["GBP"].GetProperty("absoluteValue").GetProperty("currency").GetString());
        Assert.Equal(3500.75m, decimal.Parse(cur["EUR"].GetProperty("absoluteValue").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        var reasons = s.GetProperty("byReason").EnumerateArray().Select(r => (r.GetProperty("reasonCode").GetString(), r.GetProperty("count").GetInt32())).ToArray();
        Assert.Equal([("NO_BANK_EVIDENCE", 4), ("LATE_DELIVERY", 1), ("LEDGER_GAP", 1), ("SETTLEMENT_SHORT", 1)], reasons);
        var sevCur = s.GetProperty("bySeverityCurrency").EnumerateArray()
            .Select(x => $"{x.GetProperty("severity").GetString()}/{x.GetProperty("currency").GetString()}/{x.GetProperty("count").GetInt32()}/{x.GetProperty("absoluteValue").GetProperty("amount").GetString()}").ToArray();
        Assert.Equal(["CRITICAL/EUR/1/1500.750000", "CRITICAL/GBP/1/100.500000", "HIGH/EUR/1/2000.000000", "HIGH/JPY/1/50000.000000", "MEDIUM/GBP/1/30.250000", "LOW/GBP/1/10.000000"], sevCur);
        // enum-keyed maps keep their wire names (no camel-casing of dictionary keys)
        Assert.Equal(["CRITICAL", "HIGH", "MEDIUM", "LOW"], s.GetProperty("bySeverity").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Contains("independent_ledger", s.GetProperty("byControl").EnumerateObject().Select(p => p.Name));

        var filtered = await Json(await Viewer.GetAsync(Base + "/summary?currency=GBP&status=OPEN"));
        Assert.Equal(2, filtered.GetProperty("total").GetInt32());
        Assert.Single(filtered.GetProperty("byCurrency").EnumerateArray());
    }

    [Fact]
    public async Task Detail_reads_the_recorded_rule_trace_and_never_invents_a_tolerance()
    {
        var recorded = await Json(await Viewer.GetAsync("/api/v1/exceptions/E-004"));
        var trace = recorded.GetProperty("trace");
        Assert.Equal(("NO_BANK_EVIDENCE", "matching/2", "0.01 GBP", "bank - transfer", "-30.25 GBP"),
            (trace.GetProperty("ruleId").GetString(), trace.GetProperty("ruleVersion").GetString(), trace.GetProperty("tolerance").GetString(),
             trace.GetProperty("calculation").GetString(), trace.GetProperty("result").GetString()));
        Assert.Equal(["transferAmount", "bankAmount"], trace.GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("Transfer is not backed by a bank booking", recorded.GetProperty("expectedEffect").GetString());
        Assert.Equal("No bank booking within the window", recorded.GetProperty("description").GetString());

        var derived = await Json(await Viewer.GetAsync("/api/v1/exceptions/E-001"));
        Assert.Equal(JsonValueKind.Null, derived.GetProperty("trace").GetProperty("tolerance").ValueKind); // not 'not recorded'
        Assert.Equal("rs-1", derived.GetProperty("trace").GetProperty("ruleVersion").GetString());
        // SYSTEM timeline actors carry no id
        var system = derived.GetProperty("timeline").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "SYSTEM").GetProperty("actor");
        Assert.Equal(JsonValueKind.Null, system.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Detail_has_etag_evidence_timeline_and_allowed_actions()
    {
        var res = await f.As(f.TokenFor(Role.Analyst)).GetAsync("/api/v1/exceptions/E-001");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("\"v1\"", res.Headers.ETag!.ToString());
        var d = await Json(res);
        Assert.Equal("\"v1\"", d.GetProperty("etag").GetString());
        Assert.Equal("rs-1", d.GetProperty("trace").GetProperty("ruleVersion").GetString());
        Assert.Equal(["Raised", "Assigned to Ada"], d.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("text").GetString()!).ToArray());
        Assert.Equal(["bt-1"], d.GetProperty("relatedTransactionIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["acc-9"], d.GetProperty("relatedAccountIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.True(d.GetProperty("evidenceNodes").GetArrayLength() >= 3);
        Assert.Equal(["ASSIGN", "ADD_NOTE", "REQUEST_EVIDENCE", "PROPOSE_RESOLUTION"], d.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var viewer = await Json(await Viewer.GetAsync("/api/v1/exceptions/E-001"));
        Assert.Empty(viewer.GetProperty("allowedActions").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/exceptions/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.As(null).GetAsync("/api/v1/exceptions/E-001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(f.TokenFor(Role.ConfigAdmin)).GetAsync("/api/v1/exceptions/E-001")).StatusCode);
    }

    [Fact]
    public async Task Detail_enforces_separation_of_duties_and_terminal_and_superseded_states()
    {
        var approver = f.As(f.TokenFor(Role.Approver));
        var approverId = LocalUsers.For(Role.Approver).Id;
        await f.Exec($"""
            UPDATE exceptions SET status = 'PENDING_APPROVAL' WHERE id = 'E-002';
            INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id) VALUES ('P-2','E-002','RESOLVED','fine','u2');
            INSERT INTO approvals (id, object_type, object_id, proposal_version, proposer_user_id, required_role, policy_version)
              VALUES ('A-2','EXCEPTION_RESOLUTION','P-2',1,'u2','APPROVER','pol-1');
            UPDATE exceptions SET status = 'PENDING_APPROVAL' WHERE id = 'E-004';
            INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id) VALUES ('P-4','E-004','RESOLVED','fine','{approverId}');
            INSERT INTO approvals (id, object_type, object_id, proposal_version, proposer_user_id, required_role, policy_version)
              VALUES ('A-4','EXCEPTION_RESOLUTION','P-4',1,'{approverId}','APPROVER','pol-1');
            """);
        var other = await Json(await approver.GetAsync("/api/v1/exceptions/E-002"));
        Assert.Contains("APPROVE_RESOLUTION", other.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("PROPOSE_RESOLUTION", other.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("A-2", other.GetProperty("pendingApproval").GetProperty("id").GetString());
        Assert.True(other.GetProperty("pendingApproval").GetProperty("canDecide").GetBoolean());
        Assert.Equal(["PENDING_APPROVAL"], other.GetProperty("states").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("pol-1", other.GetProperty("approvalPolicy").GetString());

        var own = await Json(await approver.GetAsync("/api/v1/exceptions/E-004"));
        Assert.DoesNotContain("APPROVE_RESOLUTION", own.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        Assert.False(own.GetProperty("pendingApproval").GetProperty("canDecide").GetBoolean());

        var closed = await Json(await f.As(f.TokenFor(Role.Analyst)).GetAsync("/api/v1/exceptions/E-007"));
        Assert.Equal(["REOPEN"], closed.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var superseded = await Json(await f.As(f.TokenFor(Role.Analyst)).GetAsync("/api/v1/exceptions/S-001"));
        Assert.Empty(superseded.GetProperty("allowedActions").EnumerateArray());
        Assert.Contains("SUPERSEDED_RUN", superseded.GetProperty("states").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Detail_etag_follows_the_version_column()
    {
        await f.Exec("UPDATE exceptions SET title = 'Edited' WHERE id = 'E-005'");
        var res = await Viewer.GetAsync("/api/v1/exceptions/E-005");
        Assert.Equal("\"v2\"", res.Headers.ETag!.ToString());
        Assert.Equal(JsonValueKind.Null, (await Json(res)).GetProperty("sourceFile").ValueKind); // null in the database stays null on the wire
    }
}
