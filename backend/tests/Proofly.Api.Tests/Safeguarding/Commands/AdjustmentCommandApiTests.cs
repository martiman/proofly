using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Safeguarding.Commands;

internal sealed class AdjCmdFactory(string schema) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Proofly", $"Host=localhost;Port=55432;Username=proofly;Database=proofly_test;Search Path={schema}");
        builder.UseSetting("Auth:Mode", "Local");
    }

    public HttpClient As(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public LocalTokenService Tokens => Services.GetRequiredService<LocalTokenService>();
}

public sealed class AdjCmdFixture : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal AdjCmdFactory Factory { get; private set; } = null!;
    internal string AnalystToken => Factory.Tokens.Issue(LocalUsers.Subject(Role.Analyst));
    internal string ApproverToken => Factory.Tokens.Issue(LocalUsers.Subject(Role.Approver));
    internal string ViewerToken => Factory.Tokens.Issue(LocalUsers.Subject(Role.Viewer));
    internal string Le2ApproverToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{_schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={_schema}");
        await new MigrationRunner(Db).RunAsync();
        await using (var c = Db.CreateCommand("""
            INSERT INTO legal_entities (id,name,base_currency) VALUES ('LE-1','One','GBP'),('LE-2','Two','EUR');
            UPDATE legal_entities SET currencies = ARRAY['GBP','EUR','JPY'] WHERE id='LE-1';
            INSERT INTO runs (id,legal_entity_id,business_date,run_version,ruleset_version,state,sign_off_state) VALUES
             ('R-A','LE-1','2026-10-27',1,'rs-1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE'),
             ('R-SIGNED','LE-1','2026-10-26',1,'rs-1','SIGNED_OFF','SIGNED_OFF'),
             ('R-B','LE-2','2026-10-27',1,'rs-1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE');
            """))
        {
            await c.ExecuteNonQueryAsync();
        }
        Factory = new AdjCmdFactory(_schema);
        _ = Factory.Server;
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-le2", "le2", "le2@x.test", "sub-le2", true, [new RoleAssignment("APPROVER", "LE-2")]));
        Le2ApproverToken = Factory.Tokens.Issue("sub-le2");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{_schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }
}

public class AdjustmentCommandApiTests(AdjCmdFixture f) : IClassFixture<AdjCmdFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static HttpRequestMessage Post(string url, string json, string? key = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (ifMatch is not null) { req.Headers.TryAddWithoutValidation("If-Match", ifMatch); }
        return req;
    }

    private static string Input(string ccy = "GBP", string amount = "-25.5", string? expires = "2026-12-31", string reason = "Timing difference", string refs = "[\"ev-1\"]") =>
        $"{{\"currency\":\"{ccy}\",\"amount\":{{\"amount\":\"{amount}\",\"currency\":\"{ccy}\"}},\"reason\":\"{reason}\",\"evidenceRefs\":{refs},\"expiresOn\":{(expires is null ? "null" : $"\"{expires}\"")}}}";

    private const string Decision = "{\"decision\":\"APPROVE\",\"reason\":\"Evidence checked\"}";

    private async Task<(string Id, string ETag)> ProposeAsync(string run = "R-A", string? token = null)
    {
        var res = await f.Factory.As(token ?? f.AnalystToken).SendAsync(Post($"/api/v1/runs/{run}/safeguarding/adjustments", Input()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return ((await Json(res)).GetProperty("id").GetString()!, res.Headers.ETag!.Tag);
    }

    private Task<long> Count(string sql) => Scalar(sql);
    private async Task<long> Scalar(string sql) { await using var c = f.Db.CreateCommand(sql); return Convert.ToInt64(await c.ExecuteScalarAsync()); }

    [Fact]
    public async Task Propose_creates_pending_adjustment_approval_and_audit_event()
    {
        var res = await f.Factory.As(f.AnalystToken).SendAsync(Post("/api/v1/runs/R-A/safeguarding/adjustments", Input()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.NotNull(res.Headers.ETag);
        var body = await Json(res);
        Assert.Equal("PROPOSED", body.GetProperty("status").GetString());
        Assert.Equal("-25.50", body.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("PENDING", body.GetProperty("approval").GetProperty("status").GetString());
        var id = body.GetProperty("id").GetString();
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_PROPOSED' AND subject_id='{id}' AND reason='Timing difference' AND chain_id='LE-1'"));
    }

    [Fact]
    public async Task Propose_replay_with_same_key_returns_same_adjustment_once()
    {
        var key = Guid.NewGuid().ToString();
        var a = await f.Factory.As(f.AnalystToken).SendAsync(Post("/api/v1/runs/R-A/safeguarding/adjustments", Input(reason: "Replay case"), key));
        var b = await f.Factory.As(f.AnalystToken).SendAsync(Post("/api/v1/runs/R-A/safeguarding/adjustments", Input(reason: "Replay case"), key));
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.Equal((await Json(a)).GetProperty("id").GetString(), (await Json(b)).GetProperty("id").GetString());
        Assert.Equal(1, await Count("SELECT count(*) FROM safeguarding_adjustments WHERE reason='Replay case'"));
    }

    [Fact]
    public async Task Propose_rejects_invalid_input_with_422_and_writes_nothing()
    {
        var before = await Count("SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_PROPOSED'");
        var c = f.Factory.As(f.AnalystToken);
        foreach (var json in new[]
        {
            Input(ccy: "USD"),                       // currency not in the run's entity
            Input(refs: "[]"),                       // evidence required
            Input(amount: "1.1234567"),              // more than 6 decimals, never rounded
            Input(amount: "0"),                      // zero
            Input(reason: "ab"),                     // reason too short
            Input(expires: "2026-10-01"),            // before business date
            Input(refs: "[\" \"]"),                  // blank evidence ref
        })
        {
            var res = await c.SendAsync(Post("/api/v1/runs/R-A/safeguarding/adjustments", json));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        }
        Assert.Equal(before, await Count("SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_PROPOSED'"));
    }

    [Fact]
    public async Task Propose_permission_scope_and_run_state()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Factory.As(f.ViewerToken).SendAsync(Post("/api/v1/runs/R-A/safeguarding/adjustments", Input()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Factory.As(f.Le2ApproverToken).SendAsync(Post("/api/v1/runs/R-A/safeguarding/adjustments", Input()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Factory.As(f.AnalystToken).SendAsync(Post("/api/v1/runs/NOPE/safeguarding/adjustments", Input()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Factory.As(f.AnalystToken).SendAsync(Post("/api/v1/runs/R-SIGNED/safeguarding/adjustments", Input()))).StatusCode);
    }

    [Fact]
    public async Task Decide_approve_updates_adjustment_approval_and_audit_in_one_transaction()
    {
        var (id, etag) = await ProposeAsync();
        var res = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEqual(etag, res.Headers.ETag!.Tag);
        var body = await Json(res);
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.Equal("APPROVED", body.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal("local-approver", body.GetProperty("approval").GetProperty("approver").GetProperty("id").GetString());
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_DECIDED' AND subject_id='{id}' AND reason='Evidence checked' AND actor_type='USER'"));
    }

    [Fact]
    public async Task Decide_reject_marks_rejected()
    {
        var (id, etag) = await ProposeAsync();
        var res = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", "{\"decision\":\"REJECT\",\"reason\":\"Not supported\"}", ifMatch: etag));
        Assert.Equal("REJECTED", (await Json(res)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Decide_requires_if_match_and_checks_it()
    {
        var (id, etag) = await ProposeAsync();
        var c = f.Factory.As(f.ApproverToken);
        Assert.Equal((HttpStatusCode)428, (await c.SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await c.SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: "\"v99\""))).StatusCode);
        Assert.Equal("PROPOSED", await Str($"SELECT status FROM safeguarding_adjustments WHERE id='{id}'"));
        Assert.Equal(0, await Count($"SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_DECIDED' AND subject_id='{id}'"));
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag))).StatusCode);
    }

    private async Task<string> Str(string sql) { await using var c = f.Db.CreateCommand(sql); return (string)(await c.ExecuteScalarAsync())!; }

    [Fact]
    public async Task Decide_idempotent_replay_returns_same_result_without_second_audit_event()
    {
        var (id, etag) = await ProposeAsync();
        var key = Guid.NewGuid().ToString();
        var a = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, key, etag));
        var b = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, key, etag));
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_DECIDED' AND subject_id='{id}'"));
    }

    [Fact]
    public async Task Decide_own_proposal_is_403_separation_of_duties_and_changes_nothing()
    {
        var (id, etag) = await ProposeAsync(token: f.ApproverToken); // approvers may propose too
        var res = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.EndsWith("separation-of-duties", (await Json(res)).GetProperty("type").GetString());
        Assert.Equal("PROPOSED", await Str($"SELECT status FROM safeguarding_adjustments WHERE id='{id}'"));
        Assert.Equal(0, await Count($"SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_DECIDED' AND subject_id='{id}'"));
    }

    [Fact]
    public async Task Decide_permission_scope_and_state_errors()
    {
        var (id, etag) = await ProposeAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Factory.As(f.AnalystToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Factory.As(f.Le2ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Factory.As(f.ApproverToken).SendAsync(Post("/api/v1/safeguarding/adjustments/NOPE/decision", Decision, ifMatch: etag))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", "{\"decision\":\"APPROVE\",\"reason\":\"x\"}", ifMatch: etag))).StatusCode);

        var ok = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag));
        var newTag = ok.Headers.ETag!.Tag;
        var again = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: newTag));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // only PROPOSED can be decided
        Assert.EndsWith("invalid-state", (await Json(again)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Decide_with_stale_proposal_version_is_409()
    {
        var (id, etag) = await ProposeAsync();
        var res = await f.Factory.As(f.ApproverToken).SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", "{\"decision\":\"APPROVE\",\"reason\":\"Checked\",\"proposalVersion\":7}", ifMatch: etag));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("PROPOSED", await Str($"SELECT status FROM safeguarding_adjustments WHERE id='{id}'"));
    }

    [Fact]
    public async Task Concurrent_decisions_with_the_same_etag_let_exactly_one_win()
    {
        var (id, etag) = await ProposeAsync();
        var c = f.Factory.As(f.ApproverToken);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => c.SendAsync(Post($"/api/v1/safeguarding/adjustments/{id}/decision", Decision, ifMatch: etag))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.PreconditionFailed, HttpStatusCode.Conflict }));
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_DECIDED' AND subject_id='{id}'"));
    }
}
