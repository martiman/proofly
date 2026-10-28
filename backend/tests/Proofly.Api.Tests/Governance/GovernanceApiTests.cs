using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Audit;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Governance;

internal sealed class GovFactory(string schema) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Proofly", $"{GovFixture.Cs};Search Path={schema}");
        builder.UseSetting("Auth:Mode", "Local");
    }
}

public sealed class GovFixture : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal GovFactory Factory { get; private set; } = null!;
    internal string ScopedToken = "";

    public async Task Exec(string sql) { await using var c = Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs)) { await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\""); await c.ExecuteNonQueryAsync(); }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={Schema}");
        await new MigrationRunner(Db).RunAsync();
        Factory = new GovFactory(Schema);
        _ = Factory.Server;
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-scoped", "Scoped Approver", "scoped@x.test", "sub-scoped", true,
            [new RoleAssignment("APPROVER", "LE1"), new RoleAssignment("CONFIG_ADMIN", "LE1")]));
        ScopedToken = Factory.Services.GetRequiredService<LocalTokenService>().Issue("sub-scoped");

        await Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE1','Entity One','GBP'),('LE2','Entity Two','EUR');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES
              ('R1','LE1','2026-10-27',1,'v1','REVIEW_REQUIRED'),('R2','LE2','2026-10-27',1,'v1','REVIEW_REQUIRED'),
              ('R3','LE1','2026-10-28',1,'v1','REVIEW_REQUIRED'),('R4','LE1','2026-10-29',1,'v1','REVIEW_REQUIRED'),
              ('R5','LE1','2026-10-30',1,'v1','REVIEW_REQUIRED');
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, policy_version, proposed_at, evidence_refs) VALUES
              ('A1','EXCEPTION_RESOLUTION','F-1','R1',1,'local-analyst','APPROVER','PENDING','p1','2026-10-27T10:00:00Z','["ev-1"]'),
              ('A2','SAFEGUARDING_ADJUSTMENT','ADJ-1','R1',1,'local-approver','APPROVER','PENDING','p1','2026-10-27T11:00:00Z','[]'),
              ('A3','EXCEPTION_RESOLUTION','F-2','R2',1,'local-analyst','APPROVER','PENDING','p1','2026-10-27T12:00:00Z','[]'),
              ('A4','EXCEPTION_RESOLUTION','F-3','R1',1,'local-analyst','APPROVER','APPROVED','p1','2026-10-27T09:00:00Z','[]'),
              ('A5','CONFIG_CHANGE','CC-1',NULL,1,'local-analyst','CONFIG_ADMIN','PENDING','p1','2026-10-27T08:00:00Z','[]');
            UPDATE approvals SET approver_user_id='local-approver', decision='APPROVE', decided_at='2026-10-27T09:30:00Z' WHERE id='A4';
            INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES
              ('RULE-1','rule','Totals match','3','{"control":"data_completeness","description":"Delivery totals match","tolerance":"0.01 GBP","severity":"HIGH","effectiveFrom":"2026-01-01"}'),
              ('SRC-1','source','bank_a','2','{"schemaVersion":"1.2","expectedByLocalTime":"08:00","mandatory":true}'),
              ('POL-1','approval_policy','Resolution approval','5','{"description":"Second person","requiredRole":"APPROVER"}'),
              ('freshness_policy','setting','Freshness','1','{"text":"Evidence must arrive by 09:00 local"}');
            INSERT INTO config_changes (id, config_id, proposed_by_user_id, reason, after, status) VALUES
              ('CC-1','RULE-1','local-analyst','tighten','{}','PENDING'),('CC-0','SRC-1','local-analyst','old','{}','APPROVED');
            INSERT INTO evidence_packs (id, run_id, sha256, storage_ref) VALUES ('EP-1','R1',repeat('a',64),'store/ep1');
            INSERT INTO jobs (type, run_id, state) VALUES ('evidence_pack.generate','R3','RUNNING'),('evidence_pack.generate','R4','DEAD');
            """);

        var w = new AuditWriter();
        async Task Ev(string type, string? run, string subj)
        {
            await using var c = await Db.OpenConnectionAsync(); await using var tx = await c.BeginTransactionAsync();
            await w.AppendAsync(tx, new AuditEventInput(type, AuditActor.System(), run is null ? "CONFIG" : "RUN", subj, "s " + subj, Reason: "seed event", RunId: run));
            await tx.CommitAsync();
        }
        await Ev("RUN_STARTED", "R1", "R1"); await Ev("RUN_STARTED", "R2", "R2"); await Ev("CONFIG_CHANGE_PROPOSED", null, "CC-1"); await Ev("CONTROL_EXECUTED", "R1", "R1");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync(); await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs); await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE"); await c.ExecuteNonQueryAsync();
    }
}

public class GovernanceApiTests(GovFixture f) : IClassFixture<GovFixture>
{
    private HttpClient As(string? token) { var c = f.Factory.CreateClient(); if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private HttpClient As(Role r) => As(f.Factory.Services.GetRequiredService<LocalTokenService>().Issue(LocalUsers.Subject(r)));
    private static async Task<JsonElement> Json(HttpResponseMessage r) { var s = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, s); return JsonDocument.Parse(s).RootElement; }
    private static string[] Ids(JsonElement items, string p = "id") => items.EnumerateArray().Select(i => i.GetProperty(p).GetString()!).ToArray();

    [Fact]
    public async Task Audit_lists_newest_first_filters_and_pages()
    {
        var all = await Json(await As(Role.Viewer).GetAsync("/api/v1/audit-events"));
        Assert.Equal(4, all.GetProperty("items").GetArrayLength());
        var p1 = await Json(await As(Role.Viewer).GetAsync("/api/v1/audit-events?limit=3&type=RUN_STARTED"));
        Assert.Equal(2, p1.GetProperty("items").GetArrayLength());
        var a = await Json(await As(Role.Viewer).GetAsync("/api/v1/audit-events?limit=3"));
        Assert.True(a.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var cursor = a.GetProperty("page").GetProperty("nextCursor").GetString();
        var b = await Json(await As(Role.Viewer).GetAsync("/api/v1/audit-events?limit=3&cursor=" + Uri.EscapeDataString(cursor!)));
        Assert.Equal(1, b.GetProperty("items").GetArrayLength());
        Assert.False(b.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Empty((await Json(await As(Role.Viewer).GetAsync("/api/v1/audit-events?runId=nope"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Audit_is_scoped_and_permissioned()
    {
        var scoped = await Json(await As(f.ScopedToken).GetAsync("/api/v1/audit-events"));
        Assert.Equal(3, scoped.GetProperty("items").GetArrayLength()); // R2 event hidden
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Role.Analyst).GetAsync("/api/v1/audit-events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As((string?)null).GetAsync("/api/v1/audit-events")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await As(Role.Viewer).GetAsync("/api/v1/audit-events?type=BOGUS")).StatusCode);
    }

    [Fact]
    public async Task Settings_returns_config_users_policies_and_pending_count()
    {
        var s = await Json(await As(Role.ConfigAdmin).GetAsync("/api/v1/settings"));
        Assert.Equal("LE1", s.GetProperty("legalEntity").GetProperty("id").GetString());
        Assert.Equal("RULE-1", s.GetProperty("rules")[0].GetProperty("ruleId").GetString());
        Assert.Equal("CC-1", s.GetProperty("rules")[0].GetProperty("pendingChangeId").GetString());
        Assert.Equal("data_completeness", s.GetProperty("rules")[0].GetProperty("control").GetString());
        Assert.Equal("bank_a", s.GetProperty("sources")[0].GetProperty("sourceName").GetString());
        Assert.Equal("Evidence must arrive by 09:00 local", s.GetProperty("freshnessPolicy").GetString());
        Assert.Equal("APPROVER", s.GetProperty("approvalPolicies")[0].GetProperty("requiredRole").GetString());
        Assert.Equal(1, s.GetProperty("pendingChanges").GetInt32());
        Assert.Contains("u-scoped", Ids(s.GetProperty("users")));
        var le2 = await Json(await As(Role.ConfigAdmin).GetAsync("/api/v1/settings?legalEntityId=LE2"));
        Assert.Equal("LE2", le2.GetProperty("legalEntity").GetProperty("id").GetString());
        Assert.DoesNotContain("u-scoped", Ids(le2.GetProperty("users")));
    }

    [Fact]
    public async Task Settings_scope_permission_and_auth()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await As(f.ScopedToken).GetAsync("/api/v1/settings?legalEntityId=LE2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(Role.ConfigAdmin).GetAsync("/api/v1/settings?legalEntityId=NOPE")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(f.ScopedToken).GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Role.Viewer).GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As((string?)null).GetAsync("/api/v1/settings")).StatusCode);
    }

    [Fact]
    public async Task Approvals_inbox_excludes_own_proposals_and_pages()
    {
        var mine = await Json(await As(Role.Approver).GetAsync("/api/v1/approvals"));
        Assert.Equal(["A3", "A1"], Ids(mine.GetProperty("items")));
        Assert.All(mine.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("canDecide").GetBoolean()));
        var p1 = await Json(await As(Role.Approver).GetAsync("/api/v1/approvals?limit=1"));
        Assert.Equal(["A3"], Ids(p1.GetProperty("items")));
        var cur = Uri.EscapeDataString(p1.GetProperty("page").GetProperty("nextCursor").GetString()!);
        var p2 = await Json(await As(Role.Approver).GetAsync("/api/v1/approvals?limit=1&cursor=" + cur));
        Assert.Equal(["A1"], Ids(p2.GetProperty("items")));
        Assert.False(p2.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal("ev-1", p2.GetProperty("items")[0].GetProperty("evidenceRefs")[0].GetString());
    }

    [Fact]
    public async Task Approvals_all_filter_scope_and_empty()
    {
        var all = await Json(await As(Role.Approver).GetAsync("/api/v1/approvals?mine=false"));
        Assert.Equal(["A3", "A2", "A1", "A4", "A5"], Ids(all.GetProperty("items")));
        Assert.False(all.GetProperty("items")[1].GetProperty("canDecide").GetBoolean()); // own proposal
        Assert.False(all.GetProperty("items")[3].GetProperty("canDecide").GetBoolean()); // not pending
        var typed = await Json(await As(Role.Approver).GetAsync("/api/v1/approvals?mine=false&objectType=CONFIG_CHANGE"));
        Assert.Equal(["A5"], Ids(typed.GetProperty("items")));
        var scoped = await Json(await As(f.ScopedToken).GetAsync("/api/v1/approvals"));
        Assert.Equal(["A2", "A1", "A5"], Ids(scoped.GetProperty("items"))); // R2 hidden; run-less A5 decidable via CONFIG_ADMIN
        var none = await Json(await As(Role.Viewer).GetAsync("/api/v1/approvals"));
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await As((string?)null).GetAsync("/api/v1/approvals")).StatusCode);
    }

    [Fact]
    public async Task EvidencePack_statuses_scope_and_auth()
    {
        var ready = await Json(await As(Role.Viewer).GetAsync("/api/v1/runs/R1/evidence-pack"));
        Assert.Equal("GENERATED", ready.GetProperty("status").GetString());
        Assert.Equal(new string('a', 64), ready.GetProperty("sha256").GetString());
        Assert.Equal("GENERATING", (await Json(await As(Role.Viewer).GetAsync("/api/v1/runs/R3/evidence-pack"))).GetProperty("status").GetString());
        Assert.Equal("FAILED", (await Json(await As(Role.Viewer).GetAsync("/api/v1/runs/R4/evidence-pack"))).GetProperty("status").GetString());
        var none = await Json(await As(Role.Viewer).GetAsync("/api/v1/runs/R5/evidence-pack"));
        Assert.Equal("NOT_GENERATED", none.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("generatedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await As(f.ScopedToken).GetAsync("/api/v1/runs/R2/evidence-pack")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(Role.Viewer).GetAsync("/api/v1/runs/nope/evidence-pack")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Role.ConfigAdmin).GetAsync("/api/v1/runs/R1/evidence-pack")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As((string?)null).GetAsync("/api/v1/runs/R1/evidence-pack")).StatusCode);
    }
}
