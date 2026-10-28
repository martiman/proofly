using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Data.Governance.Commands;

namespace Proofly.Api.Tests.Governance;

internal sealed class CfgFactory(string schema) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Proofly", $"{GovFixture.Cs};Search Path={schema}");
        builder.UseSetting("Auth:Mode", "Local");
    }
}

public sealed class CfgFixture : IAsyncLifetime
{
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal CfgFactory Factory { get; private set; } = null!;

    public async Task Exec(string sql) { await using var c = Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
    public async Task<long> Count(string sql) { await using var c = Db.CreateCommand(sql); return Convert.ToInt64(await c.ExecuteScalarAsync()); }
    public async Task<string?> Text(string sql) { await using var c = Db.CreateCommand(sql); return await c.ExecuteScalarAsync() as string; }

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(GovFixture.Cs)) { await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\""); await c.ExecuteNonQueryAsync(); }
        Db = NpgsqlDataSource.Create($"{GovFixture.Cs};Search Path={Schema}");
        await new MigrationRunner(Db).RunAsync();
        Factory = new CfgFactory(Schema);
        _ = Factory.Server;
        var users = new UserDirectory(Db);
        async Task U(string id, params (string Role, string Le)[] a) => await users.EnsureUserAsync(new UserRecord(id, id, id + "@x.test", "sub-" + id, true, a.Select(x => new RoleAssignment(x.Role, x.Le)).ToArray()));
        await U("ca1", ("CONFIG_ADMIN", "*"));
        await U("ca2", ("CONFIG_ADMIN", "*"));
        await U("sa1", ("SECURITY_ADMIN", "*"), ("CONFIG_ADMIN", "*"));
        await U("sa2", ("SECURITY_ADMIN", "*"), ("CONFIG_ADMIN", "*"));
        await U("sa-le1", ("SECURITY_ADMIN", "LE1"), ("CONFIG_ADMIN", "LE1"));
        await U("analyst", ("ANALYST", "*"));
        await U("target", ("ANALYST", "LE1"));
        await Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE1','Entity One','GBP'),('LE2','Entity Two','EUR');
            INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES
              ('RULE-1','rule','Totals','v1','{"control":"data_completeness","description":"d","tolerance":"0.01 GBP","severity":"HIGH","effectiveFrom":"2026-01-01"}'),
              ('RULE-2','rule','Totals 2','v1','{"control":"matching","description":"d","tolerance":"0.01 JPY","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('RULE-3','rule','Totals 3','v1','{"control":"matching","description":"d","tolerance":"0.01 EUR","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('RULE-4','rule','Totals 4','v1','{"control":"matching","description":"d","tolerance":"0.01 EUR","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('RULE-5','rule','Totals 5','v1','{"control":"matching","description":"d","tolerance":"0.01 EUR","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('RULE-6','rule','Totals 6','v1','{"control":"matching","description":"d","tolerance":"0.01 EUR","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('RULE-8','rule','Totals 8','v1','{"control":"matching","description":"d","tolerance":"0.01 EUR","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('RULE-7','rule','Totals 7','v1','{"control":"matching","description":"d","tolerance":"0.01 EUR","severity":"LOW","effectiveFrom":"2026-01-01"}'),
              ('SRC-1','source','bank_a','v1','{"schemaVersion":"1.2","expectedByLocalTime":"08:00","mandatory":true}'),
              ('POL-1','approval_policy','Resolution approval','v1','{"description":"Second person","requiredRole":"APPROVER"}');
            """);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync(); await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(GovFixture.Cs); await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE"); await c.ExecuteNonQueryAsync();
    }
}

public class ConfigChangeCommandTests(CfgFixture f) : IClassFixture<CfgFixture>
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    private HttpClient As(string user)
    {
        var c = f.Factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Factory.Services.GetRequiredService<LocalTokenService>().Issue("sub-" + user));
        return c;
    }
    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode code) { var s = await r.Content.ReadAsStringAsync(); Assert.True(r.StatusCode == code, s); return JsonDocument.Parse(s).RootElement; }

    private static HttpRequestMessage Propose(object body, string? key = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/changes") { Content = JsonContent.Create(body) };
        m.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return m;
    }
    private static object RuleBody(string id, string tolerance = "0.50 GBP", string? date = null, string reason = "tighten tolerance") => new
    {
        targetKind = "RULE", targetId = id, effectiveFrom = date ?? Today, reason,
        after = new { control = "matching", description = "d", tolerance, severity = "LOW", effectiveFrom = "2026-01-01" },
    };
    private static HttpRequestMessage Decide(string id, string decision, string? etag, string reason = "reviewed", string? key = null, int? pv = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/settings/changes/{id}/decision")
        { Content = JsonContent.Create(pv is null ? (object)new { decision, reason } : new { decision, reason, proposalVersion = pv }) };
        m.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (etag is not null) { m.Headers.TryAddWithoutValidation("If-Match", etag); }
        return m;
    }
    private async Task<string> ProposeOk(string user, object body)
    {
        var r = await As(user).SendAsync(Propose(body));
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetString()!;
    }
    private static object AccessBody(string user, string[] roles, string[] les) => new
    { targetKind = "USER_ACCESS", targetId = user, effectiveFrom = Today, reason = "grant access", after = new { roles, legalEntityIds = les } };

    [Fact]
    public async Task Propose_creates_pending_change_with_etag_diff_approval_and_audit()
    {
        var r = await As("ca1").SendAsync(Propose(RuleBody("RULE-1")));
        var j = await Ok(r, HttpStatusCode.Created);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        Assert.Equal("PENDING_APPROVAL", j.GetProperty("status").GetString());
        Assert.True(j.GetProperty("material").GetBoolean());
        Assert.Equal("ca1", j.GetProperty("proposer").GetProperty("id").GetString());
        var diff = j.GetProperty("diff").EnumerateArray().ToDictionary(d => d.GetProperty("path").GetString()!);
        Assert.Equal("0.01 GBP", diff["tolerance"].GetProperty("from").GetString());
        Assert.Equal("0.50 GBP", diff["tolerance"].GetProperty("to").GetString());
        Assert.Equal("CONFIG_ADMIN", j.GetProperty("approval").GetProperty("requiredRole").GetString());
        var id = j.GetProperty("id").GetString();
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='CONFIG_CHANGE_PROPOSED' AND subject_id='{id}' AND actor_id='ca1' AND reason='tighten tolerance'"));
        // config untouched until approval
        Assert.Contains("0.01 GBP", (await f.Text("SELECT payload::text FROM config_rules WHERE id='RULE-1'"))!);
        // the pending change is visible on the read side
        var s = await Ok(await As("ca1").GetAsync("/api/v1/settings"), HttpStatusCode.OK);
        Assert.True(s.GetProperty("pendingChanges").GetInt32() >= 1);
        Assert.Contains(s.GetProperty("rules").EnumerateArray(), x => x.GetProperty("ruleId").GetString() == "RULE-1" && x.GetProperty("pendingChangeId").GetString() == id);
    }

    [Fact]
    public async Task Propose_rejects_invalid_input_without_audit_or_row()
    {
        var before = await f.Count("SELECT count(*) FROM config_changes");
        var audits = await f.Count("SELECT count(*) FROM audit_events");
        async Task Expect(object body, HttpStatusCode code, string? user = "ca1") => Assert.Equal(code, (await As(user!).SendAsync(Propose(body))).StatusCode);
        await Expect(new { targetKind = "RULE", targetId = "RULE-2", effectiveFrom = Today, reason = "tighten", after = new { control = "matching", description = "d", tolerance = "0.5 GBP", severity = "LOW", effectiveFrom = "2026-01-01", bogus = 1 } }, (HttpStatusCode)422);
        await Expect(RuleBody("RULE-2", tolerance: "-0.50 GBP"), (HttpStatusCode)422);
        await Expect(RuleBody("RULE-2", tolerance: "0.5"), (HttpStatusCode)422);
        await Expect(RuleBody("RULE-2", date: "2001-01-01"), (HttpStatusCode)422);
        await Expect(RuleBody("RULE-2", reason: "ab"), (HttpStatusCode)422);
        await Expect(RuleBody("NOPE"), (HttpStatusCode)422);
        await Expect(RuleBody("RULE-2", tolerance: "0.01 JPY"), (HttpStatusCode)422); // equals current
        await Expect(new { targetKind = "RULE", targetId = "RULE-2", effectiveFrom = Today, reason = "tighten", after = new { control = "bogus", description = "d", tolerance = "1 GBP", severity = "LOW", effectiveFrom = "2026-01-01" } }, (HttpStatusCode)422);
        await Expect(RuleBody("RULE-2"), HttpStatusCode.Forbidden, "analyst");
        Assert.Equal(before, await f.Count("SELECT count(*) FROM config_changes"));
        Assert.Equal(audits, await f.Count("SELECT count(*) FROM audit_events"));
    }

    [Fact]
    public async Task Second_pending_change_for_same_target_is_409_and_writes_no_audit()
    {
        await ProposeOk("ca1", RuleBody("RULE-3"));
        var audits = await f.Count("SELECT count(*) FROM audit_events");
        var r = await As("ca2").SendAsync(Propose(RuleBody("RULE-3", tolerance: "0.70 GBP")));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(audits, await f.Count("SELECT count(*) FROM audit_events"));
        Assert.Equal(1, await f.Count("SELECT count(*) FROM config_changes WHERE target_id='RULE-3'"));
        Assert.Equal(1, await f.Count("SELECT count(*) FROM approvals WHERE object_type='CONFIG_CHANGE' AND object_id IN (SELECT id FROM config_changes WHERE target_id='RULE-3')"));
    }

    [Fact]
    public async Task Idempotent_replay_returns_same_change()
    {
        var key = Guid.NewGuid().ToString();
        var a = await Ok(await As("ca1").SendAsync(Propose(RuleBody("RULE-4"), key)), HttpStatusCode.Created);
        var r2 = await As("ca1").SendAsync(Propose(RuleBody("RULE-4"), key));
        var b = await Ok(r2, HttpStatusCode.Created);
        Assert.Equal(a.GetProperty("id").GetString(), b.GetProperty("id").GetString());
        Assert.Equal(1, await f.Count("SELECT count(*) FROM config_changes WHERE target_id='RULE-4'"));
    }

    [Fact]
    public async Task Approve_activates_bumps_ruleset_and_never_affects_earlier_runs()
    {
        var resolver = f.Factory.Services.GetRequiredService<IRulesetResolver>();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var id = await ProposeOk("ca1", RuleBody("RULE-8", "0.75 GBP"));
        var approver = As("ca2");
        var r = await approver.SendAsync(Decide(id, "APPROVE", "\"v1\"", pv: 1));
        var j = await Ok(r, HttpStatusCode.OK);
        Assert.Equal("ACTIVE", j.GetProperty("status").GetString());
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
        Assert.Equal("APPROVED", j.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal("ca2", j.GetProperty("approval").GetProperty("approver").GetProperty("id").GetString());

        var rule = (await f.Text("SELECT payload::text FROM config_rules WHERE id='RULE-8'"))!;
        Assert.Contains("0.75 GBP", rule);
        var rsv = await f.Text("SELECT ruleset_version FROM config_changes WHERE id='" + id + "'");
        Assert.NotNull(rsv);
        Assert.Equal(rsv, await f.Text("SELECT config_version FROM config_rules WHERE id='RULE-8'"));
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='CONFIG_CHANGE_DECIDED' AND subject_id='{id}' AND actor_id='ca2' AND reason='reviewed'"));

        // ruleset resolution: a run that started before the approval keeps the old version, a later run gets the new one
        Assert.Equal("v1", (await resolver.ResolveAsync(before)).Version);
        var now = await resolver.ResolveAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(rsv, now.Version);
        Assert.Equal(id, now.ChangeId);
        Assert.Contains("0.75 GBP", now.SnapshotJson);

        // settings read side shows the new value
        var s = await Ok(await As("ca1").GetAsync("/api/v1/settings"), HttpStatusCode.OK);
        Assert.Contains(s.GetProperty("rules").EnumerateArray(), x => x.GetProperty("ruleId").GetString() == "RULE-8" && x.GetProperty("tolerance").GetString() == "0.75 GBP");
    }

    [Fact]
    public async Task Future_effective_date_is_not_resolved_before_it_is_reached()
    {
        var resolver = f.Factory.Services.GetRequiredService<IRulesetResolver>();
        var future = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        var id = await ProposeOk("ca1", RuleBody("RULE-6", "0.90 GBP", future));
        await Ok(await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\"")), HttpStatusCode.OK);
        var v = await f.Text($"SELECT ruleset_version FROM config_changes WHERE id='{id}'");
        Assert.NotEqual(v, (await resolver.ResolveAsync(DateTimeOffset.UtcNow.AddMinutes(1))).Version);
        Assert.Equal(v, (await resolver.ResolveAsync(DateTimeOffset.UtcNow.AddDays(10).AddMinutes(1))).Version);
    }

    [Fact]
    public async Task Approving_a_newer_change_retires_the_previous_active_one()
    {
        var a = await ProposeOk("ca1", RuleBody("RULE-7", "0.11 GBP"));
        await Ok(await As("ca2").SendAsync(Decide(a, "APPROVE", "\"v1\"")), HttpStatusCode.OK);
        var b = await ProposeOk("ca1", RuleBody("RULE-7", "0.12 GBP"));
        await Ok(await As("ca2").SendAsync(Decide(b, "APPROVE", "\"v1\"")), HttpStatusCode.OK);
        Assert.Equal("RETIRED", await f.Text($"SELECT status FROM config_changes WHERE id='{a}'"));
        Assert.Equal("ACTIVE", await f.Text($"SELECT status FROM config_changes WHERE id='{b}'"));
    }

    [Fact]
    public async Task Reject_leaves_configuration_and_rulesets_untouched()
    {
        var rulesets = await f.Count("SELECT count(*) FROM ruleset_versions");
        var id = await ProposeOk("ca1", RuleBody("RULE-5", "0.33 GBP"));
        var r = await As("ca2").SendAsync(Decide(id, "REJECT", "\"v1\"", "not justified"));
        var j = await Ok(r, HttpStatusCode.OK);
        Assert.Equal("REJECTED", j.GetProperty("status").GetString());
        Assert.Equal("REJECT", j.GetProperty("approval").GetProperty("decision").GetString());
        Assert.Contains("0.01 EUR", (await f.Text("SELECT payload::text FROM config_rules WHERE id='RULE-5'"))!);
        Assert.Equal(rulesets, await f.Count("SELECT count(*) FROM ruleset_versions"));
        // immutable once decided
        Assert.Equal(HttpStatusCode.Conflict, (await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v2\""))).StatusCode);
        Assert.Equal("REJECTED", await f.Text($"SELECT status FROM config_changes WHERE id='{id}'"));
    }

    [Fact]
    public async Task Decide_enforces_etag_separation_of_duties_permission_and_existence()
    {
        var id = await ProposeOk("ca1", RuleBody("RULE-2"));
        Assert.Equal((HttpStatusCode)428, (await As("ca2").SendAsync(Decide(id, "APPROVE", null))).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v9\""))).StatusCode);
        var sod = await As("ca1").SendAsync(Decide(id, "APPROVE", "\"v1\""));
        Assert.Equal(HttpStatusCode.Forbidden, sod.StatusCode);
        Assert.EndsWith("separation-of-duties", (await Body(sod)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await As("analyst").SendAsync(Decide(id, "APPROVE", "\"v1\""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("ca2").SendAsync(Decide("CC-NOPE", "APPROVE", "\"v1\""))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\"", reason: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\"", pv: 7))).StatusCode);
        Assert.Equal("PENDING_APPROVAL", await f.Text($"SELECT status FROM config_changes WHERE id='{id}'"));
        Assert.Equal(0, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='CONFIG_CHANGE_DECIDED' AND subject_id='{id}'"));
    }

    [Fact]
    public async Task Concurrent_decisions_yield_exactly_one_winner()
    {
        await f.Exec("INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('RULE-C','rule','Conc','v1','{\"control\":\"matching\",\"description\":\"d\",\"tolerance\":\"0.01 EUR\",\"severity\":\"LOW\",\"effectiveFrom\":\"2026-01-01\"}')");
        var id = await ProposeOk("ca1", RuleBody("RULE-C"));
        var tasks = new[] { "ca2", "sa1", "sa2", "ca2" }.Select(u => As(u).SendAsync(Decide(id, "APPROVE", "\"v1\""))).ToArray();
        var codes = (await Task.WhenAll(tasks)).Select(r => r.StatusCode).ToList();
        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.OK));
        Assert.All(codes.Where(c => c != HttpStatusCode.OK), c => Assert.True(c is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed, c.ToString()));
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='CONFIG_CHANGE_DECIDED' AND subject_id='{id}'"));
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM ruleset_versions WHERE change_id='{id}'"));
    }

    [Fact]
    public async Task Decide_is_idempotent_on_replay()
    {
        await f.Exec("INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('RULE-I','rule','Idem','v1','{\"control\":\"matching\",\"description\":\"d\",\"tolerance\":\"0.01 EUR\",\"severity\":\"LOW\",\"effectiveFrom\":\"2026-01-01\"}')");
        var id = await ProposeOk("ca1", RuleBody("RULE-I"));
        var key = Guid.NewGuid().ToString();
        await Ok(await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\"", key: key)), HttpStatusCode.OK);
        await Ok(await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\"", key: key)), HttpStatusCode.OK);
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='CONFIG_CHANGE_DECIDED' AND subject_id='{id}'"));
    }

    [Fact]
    public async Task Source_and_policy_changes_validate_their_shape()
    {
        object Src(object after) => new { targetKind = "SOURCE", targetId = "SRC-1", effectiveFrom = Today, reason = "new schema", after };
        Assert.Equal((HttpStatusCode)422, (await As("ca1").SendAsync(Propose(Src(new { schemaVersion = "1.3", expectedByLocalTime = "25:00", mandatory = true })))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As("ca1").SendAsync(Propose(Src(new { schemaVersion = "1.3", expectedByLocalTime = "08:00" })))).StatusCode);
        var pol = new { targetKind = "APPROVAL_POLICY", targetId = "POL-1", effectiveFrom = Today, reason = "new role", after = new { description = "x", requiredRole = "NOPE" } };
        Assert.Equal((HttpStatusCode)422, (await As("ca1").SendAsync(Propose(pol))).StatusCode);
        var id = await ProposeOk("ca1", Src(new { schemaVersion = "1.3", expectedByLocalTime = "08:30", mandatory = false }));
        await Ok(await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\"")), HttpStatusCode.OK);
        var s = await Ok(await As("ca1").GetAsync("/api/v1/settings"), HttpStatusCode.OK);
        Assert.Contains(s.GetProperty("sources").EnumerateArray(), x => x.GetProperty("schemaVersion").GetString() == "1.3" && !x.GetProperty("mandatory").GetBoolean());
    }

    [Fact]
    public async Task Access_change_updates_roles_only_on_approval_atomically_and_audits()
    {
        var id = await ProposeOk("sa1", AccessBody("target", ["ANALYST", "VIEWER"], ["LE1", "LE2"]));
        Assert.Equal(1, await f.Count("SELECT count(*) FROM user_roles WHERE user_id='target'"));
        Assert.Equal("SECURITY_ADMIN", await f.Text($"SELECT required_role FROM approvals WHERE object_id='{id}' AND object_type='ACCESS_CHANGE'"));
        var s = await Ok(await As("sa1").GetAsync("/api/v1/settings"), HttpStatusCode.OK);
        Assert.Contains(s.GetProperty("users").EnumerateArray(), u => u.GetProperty("id").GetString() == "target" && u.GetProperty("pendingChangeId").GetString() == id);

        Assert.Equal(HttpStatusCode.Forbidden, (await As("sa1").SendAsync(Decide(id, "APPROVE", "\"v1\""))).StatusCode); // proposer
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ca2").SendAsync(Decide(id, "APPROVE", "\"v1\""))).StatusCode); // settings.approve but no access.manage
        var r = await As("sa2").SendAsync(Decide(id, "APPROVE", "\"v1\""));
        var j = await Ok(r, HttpStatusCode.OK);
        Assert.Equal("ACTIVE", j.GetProperty("status").GetString());
        Assert.Equal(4, await f.Count("SELECT count(*) FROM user_roles WHERE user_id='target'"));
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='ACCESS_CHANGED' AND subject_id='{id}' AND actor_id='sa2'"));
        Assert.Equal(1, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='CONFIG_CHANGE_DECIDED' AND subject_id='{id}'"));
        Assert.Null(await f.Text($"SELECT ruleset_version FROM config_changes WHERE id='{id}'")); // access changes do not version the ruleset
    }

    [Fact]
    public async Task Rejected_access_change_keeps_roles()
    {
        await f.Exec("INSERT INTO users (id, name, email) VALUES ('target2','t2','t2@x.test'); INSERT INTO user_roles VALUES ('target2','VIEWER','LE1')");
        var id = await ProposeOk("sa1", AccessBody("target2", ["APPROVER"], ["LE1"]));
        await Ok(await As("sa2").SendAsync(Decide(id, "REJECT", "\"v1\"", "too broad")), HttpStatusCode.OK);
        Assert.Equal("VIEWER", await f.Text("SELECT role_id FROM user_roles WHERE user_id='target2'"));
        Assert.Equal(0, await f.Count($"SELECT count(*) FROM audit_events WHERE event_type='ACCESS_CHANGED' AND subject_id='{id}'"));
    }

    [Fact]
    public async Task Access_change_respects_legal_entity_scope_and_validity()
    {
        // scoped admin cannot touch LE2
        Assert.Equal(HttpStatusCode.NotFound, (await As("sa-le1").SendAsync(Propose(AccessBody("target", ["ANALYST"], ["LE2"])))).StatusCode);
        // permission: a plain config admin lacks access.manage
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ca1").SendAsync(Propose(AccessBody("target", ["ANALYST"], ["LE1"])))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As("sa1").SendAsync(Propose(AccessBody("target", ["GOD"], ["LE1"])))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As("sa1").SendAsync(Propose(AccessBody("target", ["ANALYST"], ["LE9"])))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As("sa1").SendAsync(Propose(AccessBody("nobody", ["ANALYST"], ["LE1"])))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As("sa1").SendAsync(Propose(AccessBody("target", ["ANALYST"], [])))).StatusCode);
    }

    [Fact]
    public async Task Access_change_is_stale_when_the_user_changed_meanwhile()
    {
        await f.Exec("INSERT INTO users (id, name, email) VALUES ('target3','t3','t3@x.test'); INSERT INTO user_roles VALUES ('target3','VIEWER','LE1')");
        var id = await ProposeOk("sa1", AccessBody("target3", ["ANALYST"], ["LE1"]));
        await f.Exec("INSERT INTO user_roles VALUES ('target3','ANALYST','LE2')");
        Assert.Equal(HttpStatusCode.Conflict, (await As("sa2").SendAsync(Decide(id, "APPROVE", "\"v1\""))).StatusCode);
        Assert.Equal(2, await f.Count("SELECT count(*) FROM user_roles WHERE user_id='target3'"));
        Assert.Equal("PENDING_APPROVAL", await f.Text($"SELECT status FROM config_changes WHERE id='{id}'"));
    }

    [Fact]
    public async Task List_and_get_filter_and_page()
    {
        var ids = new List<string>();
        await f.Exec("INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('RULE-L','rule','List','v1','{\"control\":\"matching\",\"description\":\"d\",\"tolerance\":\"0.01 EUR\",\"severity\":\"LOW\",\"effectiveFrom\":\"2026-01-01\"}')");
        ids.Add(await ProposeOk("ca1", RuleBody("RULE-L")));
        var all = await Ok(await As("ca1").GetAsync("/api/v1/settings/changes?limit=2"), HttpStatusCode.OK);
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
        Assert.True(all.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var next = all.GetProperty("page").GetProperty("nextCursor").GetString()!;
        var p2 = await Ok(await As("ca1").GetAsync("/api/v1/settings/changes?limit=2&cursor=" + Uri.EscapeDataString(next)), HttpStatusCode.OK);
        Assert.DoesNotContain(p2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()), x => all.GetProperty("items").EnumerateArray().Any(a => a.GetProperty("id").GetString() == x));
        var access = await Ok(await As("ca1").GetAsync("/api/v1/settings/changes?targetKind=USER_ACCESS&status=ACTIVE"), HttpStatusCode.OK);
        Assert.All(access.GetProperty("items").EnumerateArray(), i => { Assert.Equal("USER_ACCESS", i.GetProperty("targetKind").GetString()); Assert.Equal("ACTIVE", i.GetProperty("status").GetString()); });
        var one = await As("analyst").GetAsync($"/api/v1/settings/changes/{ids[0]}");
        Assert.Equal(HttpStatusCode.Forbidden, one.StatusCode); // analyst lacks settings.view
        var g = await As("ca2").GetAsync($"/api/v1/settings/changes/{ids[0]}");
        Assert.Equal("\"v1\"", g.Headers.ETag!.Tag);
        Assert.True((await Body(g)).GetProperty("approval").GetProperty("canDecide").GetBoolean());
        Assert.False((await Body(await As("ca1").GetAsync($"/api/v1/settings/changes/{ids[0]}"))).GetProperty("approval").GetProperty("canDecide").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await As("ca1").GetAsync("/api/v1/settings/changes/CC-NOPE")).StatusCode);
    }
}
