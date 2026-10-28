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

namespace Proofly.Api.Tests.Runs;

public sealed class RunsFixture : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal Factory F { get; private set; } = null!;

    internal sealed class Factory(string schema) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Proofly", $"{Cs};Search Path={schema}");
            builder.UseSetting("Auth:Mode", "Local");
        }

        public HttpClient As(string? token)
        {
            var c = CreateClient();
            if (token is not null) { c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
            return c;
        }

        public LocalTokenService Tokens => Services.GetRequiredService<LocalTokenService>();
    }

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{_schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={_schema}");
        await new MigrationRunner(Db).RunAsync();
        await using (var c = Db.CreateCommand(Seed)) { await c.ExecuteNonQueryAsync(); }
        F = new Factory(_schema);
        _ = F.Server;
    }

    public async Task DisposeAsync()
    {
        await F.DisposeAsync();
        await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{_schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    private const string Seed = """
        INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('LE-1','Alpha Ltd','GBP','{EUR,GBP}'), ('LE-2','Beta GmbH','EUR','{EUR}');
        INSERT INTO users (id, name, email) VALUES ('u1','Una','u1@x.test');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, started_at, last_evidence_arrival_at) VALUES
          ('R-1','LE-1','2026-10-26',1,'rs1','SUPERSEDED','SUPERSEDED','2026-10-26T08:00Z','2026-10-26T07:00Z'),
          ('R-3','LE-1','2026-10-27',1,'rs1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE','2026-10-27T08:00Z','2026-10-27T07:30Z'),
          ('R-4','LE-2','2026-10-27',1,'rs1','PROCESSING','PREPARATION_INCOMPLETE','2026-10-27T08:00Z','2026-10-27T07:45Z');
        INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, declared_record_count, expected_by, received_at, state, accepted_record_count, rejected_record_count) VALUES
          ('D-1','R-3','LE-1','bank','2026-10-27','1.0','bank.csv',repeat('a',64),12,'2026-10-27T06:00Z','2026-10-27T05:59Z','RECEIVED',10,2);
        INSERT INTO delivery_control_totals (delivery_id, currency, declared_amount) VALUES ('D-1','GBP',100.5), ('D-1','JPY',7000);
        INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state) VALUES
          ('R-3','LE-1','GBP',1000,900,-100,'SHORTFALL'), ('R-3','LE-1','EUR',500,500,0,'COVERED');
        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking, created_at) VALUES
          ('E-1','R-3','LE-1','2026-10-27','Shortfall','safeguarding','SHORTFALL','HIGH','OPEN',100,'GBP','2026-10-28T00:00Z',true,'2026-10-27T10:00Z'),
          ('E-2','R-3','LE-1','2026-10-27','No value','matching','UNMATCHED','LOW','OPEN',NULL,NULL,'2026-10-29T00:00Z',false,'2026-10-27T10:00Z');
        """;
}

public class RunsApiTests(RunsFixture f) : IClassFixture<RunsFixture>
{
    private RunsFixture.Factory F => f.F;

    private async Task<string> TokenFor(string key, params (Role Role, string Le)[] roles)
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true,
            roles.Select(r => new RoleAssignment(WireNames.ToUpperSnake(r.Role), r.Le)).ToList()));
        return F.Tokens.Issue("sub-" + key);
    }

    private HttpClient Viewer => F.As(F.Tokens.Issue(LocalUsers.Subject(Role.Viewer)));
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.NotFound, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }
    private static string[] Ids(JsonElement items) => items.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task Legal_entities_lists_the_scope_with_currencies()
    {
        var res = await Viewer.GetAsync("/api/v1/legal-entities");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Json(res);
        Assert.Equal(["LE-1", "LE-2"], Ids(body));
        Assert.Equal(["EUR", "GBP"], body[0].GetProperty("currencies").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var scoped = F.As(await TokenFor("le1", (Role.Analyst, "LE-1")));
        Assert.Equal(["LE-1"], Ids(await Json(await scoped.GetAsync("/api/v1/legal-entities"))));
    }

    [Fact]
    public async Task Runs_list_sorts_filters_and_pages_with_a_cursor()
    {
        var first = await Json(await Viewer.GetAsync("/api/v1/runs?limit=1"));
        Assert.Equal(["R-3"], Ids(first.GetProperty("items")));
        Assert.True(first.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var cursor = first.GetProperty("page").GetProperty("nextCursor").GetString();

        var second = await Json(await Viewer.GetAsync($"/api/v1/runs?limit=1&cursor={Uri.EscapeDataString(cursor!)}"));
        Assert.Equal(["R-4"], Ids(second.GetProperty("items")));
        Assert.False(second.GetProperty("page").GetProperty("hasMore").GetBoolean());

        Assert.Equal(["R-3", "R-4", "R-1"], Ids((await Json(await Viewer.GetAsync("/api/v1/runs?includeSuperseded=true"))).GetProperty("items")));
        Assert.Equal(["R-4"], Ids((await Json(await Viewer.GetAsync("/api/v1/runs?legalEntityId=LE-2&businessDate=2026-10-27"))).GetProperty("items")));
        Assert.Empty((await Json(await Viewer.GetAsync("/api/v1/runs?businessDate=2020-01-01"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Runs_list_for_an_out_of_scope_entity_is_404_and_scope_limits_results()
    {
        var scoped = F.As(await TokenFor("le2", (Role.Viewer, "LE-2")));
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync("/api/v1/runs?legalEntityId=LE-1")).StatusCode);
        Assert.Equal(["R-4"], Ids((await Json(await scoped.GetAsync("/api/v1/runs"))).GetProperty("items")));
    }

    [Fact]
    public async Task Get_run_returns_etag_and_contract_shape()
    {
        var res = await Viewer.GetAsync("/api/v1/runs/R-3");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("\"v1\"", res.Headers.ETag!.Tag);
        var run = await Json(res);
        Assert.Equal("R-3", run.GetProperty("id").GetString());
        Assert.Equal("2026-10-27", run.GetProperty("businessDate").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("supersededByRunId").ValueKind);
    }

    [Fact]
    public async Task Unknown_and_out_of_scope_runs_are_404_without_disclosure()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/nope")).StatusCode);
        var scoped = F.As(await TokenFor("le2b", (Role.Viewer, "LE-2")));
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync("/api/v1/runs/R-3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync("/api/v1/runs/R-3/control-room")).StatusCode);
    }

    [Fact]
    public async Task Control_room_matches_the_contract_with_per_currency_money()
    {
        var res = await Viewer.GetAsync("/api/v1/runs/R-3/control-room");
        var room = await Json(res); // response validation middleware checks the contract: drift is a 500
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("R-3", room.GetProperty("run").GetProperty("id").GetString());
        var d = room.GetProperty("evidenceReadiness")[0];
        Assert.Equal(["100.50", "7000.00"], d.GetProperty("declaredTotals").EnumerateArray().Select(m => m.GetProperty("amount").GetString()!).ToArray());
        Assert.Equal(["GBP", "JPY"], d.GetProperty("declaredTotals").EnumerateArray().Select(m => m.GetProperty("currency").GetString()!).ToArray());
        var sg = room.GetProperty("safeguarding").EnumerateArray().ToList();
        Assert.Equal(["EUR", "GBP"], sg.Select(s => s.GetProperty("currency").GetString()!));
        Assert.Equal("-100.00", sg[1].GetProperty("result").GetProperty("amount").GetString());
        Assert.Equal(["E-1", "E-2"], Ids(room.GetProperty("attentionQueue")));
        Assert.Equal(2, room.GetProperty("openExceptionsTotal").GetInt32());
        Assert.True(room.GetProperty("blockingCount").GetInt32() <= room.GetProperty("openExceptionsTotal").GetInt32());
        Assert.Equal("\"v1\"", room.GetProperty("attentionQueue")[0].GetProperty("etag").GetString());
        Assert.Equal(JsonValueKind.Null, room.GetProperty("attentionQueue")[1].GetProperty("value").ValueKind);
        Assert.Contains(room.GetProperty("controlAreas").EnumerateArray(), a => a.GetProperty("area").GetString() == "safeguarding" && a.GetProperty("state").GetString() == "BLOCKED");
    }

    [Fact]
    public async Task Control_room_of_an_empty_run_returns_empty_lists()
    {
        var room = await Json(await Viewer.GetAsync("/api/v1/runs/R-4/control-room"));
        foreach (var p in new[] { "summary", "evidenceReadiness", "controlAreas", "attentionQueue", "safeguarding" })
        {
            Assert.Equal(0, room.GetProperty(p).GetArrayLength());
        }
        Assert.Equal(0, room.GetProperty("blockingCount").GetInt32());
        Assert.Equal(0, room.GetProperty("openExceptionsTotal").GetInt32());
    }

    [Theory]
    [InlineData("/api/v1/legal-entities")]
    [InlineData("/api/v1/runs")]
    [InlineData("/api/v1/runs/R-3")]
    [InlineData("/api/v1/runs/R-3/control-room")]
    public async Task Unauthenticated_is_401_and_missing_permission_is_403(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(null).GetAsync(url)).StatusCode);
        // a user whose only role lacks run.view: ANALYST has it, so build one with no roles at all
        var none = F.As(await TokenFor("norole"));
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync(url)).StatusCode);
    }
}
