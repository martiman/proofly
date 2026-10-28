using System.Net;
using System.Text;
using System.Text.Json;
using Proofly.Api.Tests.Auth;
using Proofly.Data.Auth;
using Proofly.Data.Runs.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Runs;

/// <summary>Sign-off pack, submit, return and approve. Neutral GBP/EUR/JPY data, independent of examples/synthetic-data.</summary>
public sealed class SignOffApiTests(AuthFixture f) : IClassFixture<AuthFixture>, IAsyncLifetime
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;
    private AuthFactory F => f.Factory;
    private HttpClient Approver => F.As(F.TokenFor(Role.Approver));
    private HttpClient Analyst => F.As(F.TokenFor(Role.Analyst));
    private HttpClient Viewer => F.As(F.TokenFor(Role.Viewer));

    private static string Run(string id, string le, string date, string state = "READY_FOR_SIGN_OFF", string so = "READY_FOR_REVIEW", string hash = "a", bool completeness = true) => $"""
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, result_hash)
        VALUES ('{id}','{le}','{date}',1,'rs1','{state}','{so}', repeat('{hash}',64));
        {(completeness ? $"INSERT INTO run_control_results (run_id, control, state, rule_version, record_count, finding_count, blocking_finding_count) VALUES ('{id}','data_completeness','PASSED','c1',10,0,0);" : "")}
        """;

    private static string Exc(string id, string run, string le, bool blocking, string status = "OPEN") => $"""
        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking)
        VALUES ('{id}','{run}','{le}','2026-04-01','t','matching','VALUE_MISMATCH','MEDIUM','{status}',12.50,'JPY','2026-10-30T00:00Z',{(blocking ? "true" : "false")});
        """;

    public async Task InitializeAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded) { return; }
            await Exec("INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('LE-S1','Entity S1','GBP','{EUR,JPY}'), ('LE-S2','Entity S2','EUR','{}');");
            _seeded = true;
        }
        finally { SeedLock.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Exec(string sql) { await using var c = f.Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
    private async Task<T?> Scalar<T>(string sql) { await using var c = f.Db.CreateCommand(sql); var v = await c.ExecuteScalarAsync(); return v is null or DBNull ? default : (T)v; }
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static int _day;
    private static string NewId(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> User(string key, Role role, string le = "*")
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true, [new RoleAssignment(WireNames.ToUpperSnake(role), le)]));
        return F.As(F.Tokens.Issue("sub-" + key));
    }

    private static async Task<string> ETagOf(HttpClient c, string run) => (await c.GetAsync($"/api/v1/runs/{run}/sign-off")).Headers.ETag!.ToString();

    private static async Task<HttpResponseMessage> Post(HttpClient c, string run, string action, string body, string? etag, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/{action}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Submit(HttpClient c, string run, string body = "{}") => await Post(c, run, "submit", body, await ETagOf(c, run));
    private static async Task<HttpResponseMessage> Approve(HttpClient c, string run, string? etag = null, string body = """{"attestation":true,"reason":"Reviewed the pack"}""") =>
        await Post(c, run, "approve", body, etag ?? await ETagOf(c, run));

    private Task<long> Audits(string type, string run) => Scalar<long>($"SELECT count(*) FROM audit_events WHERE event_type='{type}' AND run_id='{run}'")!;

    private async Task<string> Ready(string? le = null, bool completeness = true, string state = "READY_FOR_SIGN_OFF", string so = "READY_FOR_REVIEW", params string[] extraSql)
    {
        var id = NewId("RS-");
        var date = new DateOnly(2020, 1, 1).AddDays(Interlocked.Increment(ref _day)).ToString("yyyy-MM-dd");
        await Exec(Run(id, le ?? "LE-S1", date, state, so, completeness: completeness) + string.Concat(extraSql).Replace("{run}", id));
        return id;
    }

    [Fact]
    public async Task Full_flow_submit_approve_stores_the_attestation_hash_and_audits_each_step()
    {
        var run = await Ready(extraSql: Exc("E-{run}", "{run}", "LE-S1", false, "RESOLVED"));
        var pack = await Json(await Analyst.GetAsync($"/api/v1/runs/{run}/sign-off"));
        Assert.Equal(["SUBMIT"], pack.GetProperty("allowedActions").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(pack.GetProperty("canSignOff").GetBoolean());
        Assert.Equal(64, pack.GetProperty("attestationTextHash").GetString()!.Length);

        var res = await Submit(Analyst, run, """{"note":"Ready for review"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(res.Headers.ETag);
        var body = await Json(res);
        Assert.Equal("APPROVAL_PENDING", body.GetProperty("run").GetProperty("signOffState").GetString());
        Assert.Equal("local-analyst", body.GetProperty("run").GetProperty("preparer").GetProperty("id").GetString());
        Assert.Equal("PENDING", body.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal(1, await Audits("SIGN_OFF_SUBMITTED", run));

        var seen = await Json(await Approver.GetAsync($"/api/v1/runs/{run}/sign-off"));
        Assert.True(seen.GetProperty("canSignOff").GetBoolean());
        Assert.Contains("APPROVE", seen.GetProperty("allowedActions").EnumerateArray().Select(e => e.GetString()));

        var signed = await Approve(Approver, run, body: $$"""{"attestation":true,"reason":"Reviewed the pack","attestationTextHash":"{{seen.GetProperty("attestationTextHash").GetString()}}"}""");
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var s = await Json(signed);
        Assert.Equal("SIGNED_OFF", s.GetProperty("run").GetProperty("state").GetString());
        Assert.Equal("local-approver", s.GetProperty("run").GetProperty("signedOffBy").GetProperty("id").GetString());
        Assert.Equal("APPROVED", s.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal(1, await Audits("RUN_SIGNED_OFF", run));

        var textHash = await Scalar<string>($"SELECT attestation_text_hash FROM run_signoffs WHERE run_id='{run}'");
        var stateHash = await Scalar<string>($"SELECT signed_exception_state_hash FROM run_signoffs WHERE run_id='{run}'");
        Assert.Equal(SignOffAttestation.AttestationHash(textHash!, new string('a', 64), stateHash!, "rs1"), await Scalar<string>($"SELECT attestation_hash FROM run_signoffs WHERE run_id='{run}'"));
        Assert.Equal(new string('a', 64), await Scalar<string>($"SELECT signed_result_hash FROM run_signoffs WHERE run_id='{run}'"));

        // immutable: a signed run neither re-submits nor accepts exception changes
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(Analyst, run)).StatusCode);
        await Exec($"INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, due_at) VALUES ('X-{run}','{run}','LE-S1','2026-04-01','t','matching','R','LOW','OPEN','2026-10-30T00:00Z')");
        var assign = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/exceptions/X-{run}/assign") { Content = new StringContent("""{"ownerId":"local-analyst"}""", Encoding.UTF8, "application/json") };
        assign.Headers.TryAddWithoutValidation("If-Match", (await Approver.GetAsync($"/api/v1/exceptions/X-{run}")).Headers.ETag!.ToString());
        assign.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await Approver.SendAsync(assign)).StatusCode);
    }

    [Fact]
    public async Task Submit_requires_if_match_and_a_current_etag_and_is_idempotent()
    {
        var run = await Ready();
        Assert.Equal((HttpStatusCode)428, (await Post(Analyst, run, "submit", "{}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Post(Analyst, run, "submit", "{}", "\"so-9-9\"")).StatusCode);
        Assert.Equal(0, await Audits("SIGN_OFF_SUBMITTED", run));

        var etag = await ETagOf(Analyst, run);
        var key = Guid.NewGuid().ToString();
        var first = await Post(Analyst, run, "submit", "{}", etag, key);
        var second = await Post(Analyst, run, "submit", "{}", etag, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM run_signoffs WHERE run_id='{run}'"));
        Assert.Equal(1, await Audits("SIGN_OFF_SUBMITTED", run));
    }

    [Fact]
    public async Task Permissions_scope_and_validation()
    {
        var run = await Ready();
        var etag = await ETagOf(Analyst, run);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Viewer, run, "submit", "{}", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Analyst, run, "approve", """{"attestation":true,"reason":"abcd"}""", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Viewer.GetAsync($"/api/v1/runs/{run}/sign-off")).StatusCode);

        var other = await User("s2-analyst", Role.Analyst, "LE-S2");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/runs/{run}/sign-off")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(other, run, "submit", "{}", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Analyst.GetAsync("/api/v1/runs/NOPE/sign-off")).StatusCode);

        Assert.Equal((HttpStatusCode)422, (await Post(Analyst, run, "submit", $$"""{"note":"{{new string('x', 2001)}}"}""", etag)).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Post(Analyst, run, "submit", """{"requiredApproverId":"local-viewer"}""", etag)).StatusCode);
        Assert.Equal(0, await Audits("SIGN_OFF_SUBMITTED", run));
    }

    [Fact]
    public async Task Blocking_exceptions_and_incomplete_completeness_block_submission_with_the_reasons()
    {
        var run = await Ready(extraSql: Exc("B-{run}", "{run}", "LE-S1", true));
        var res = await Submit(Analyst, run);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var p = await Json(res);
        Assert.EndsWith("invalid-state", p.GetProperty("type").GetString());
        Assert.Contains($"B-{run}", p.GetProperty("detail").GetString());
        Assert.Equal(0, await Audits("SIGN_OFF_SUBMITTED", run));
        var pack = await Json(await Analyst.GetAsync($"/api/v1/runs/{run}/sign-off"));
        Assert.Single(pack.GetProperty("blockers").EnumerateArray());
        Assert.Empty(pack.GetProperty("allowedActions").EnumerateArray());

        var noCompleteness = await Ready(completeness: false);
        Assert.Contains("completeness", await (await Submit(Analyst, noCompleteness)).Content.ReadAsStringAsync());
        var notReady = await Ready(state: "PROCESSING", so: "PREPARATION_INCOMPLETE");
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(Analyst, notReady)).StatusCode);
    }

    [Fact]
    public async Task Separation_of_duties_the_preparer_cannot_approve_or_return()
    {
        var run = await Ready();
        Assert.Equal(HttpStatusCode.OK, (await Submit(Approver, run)).StatusCode);
        var approve = await Approve(Approver, run);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.EndsWith("separation-of-duties", (await Json(approve)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Approver, run, "return", """{"reason":"Not mine to return"}""", await ETagOf(Approver, run))).StatusCode);
        Assert.Equal(0, await Audits("RUN_SIGNED_OFF", run));
        var pack = await Json(await Approver.GetAsync($"/api/v1/runs/{run}/sign-off"));
        Assert.False(pack.GetProperty("canSignOff").GetBoolean());
        Assert.Equal("APPROVAL_PENDING", pack.GetProperty("run").GetProperty("signOffState").GetString());
    }

    [Fact]
    public async Task Approve_needs_attestation_and_a_submitted_run()
    {
        var run = await Ready();
        Assert.Equal(HttpStatusCode.Conflict, (await Approve(Approver, run)).StatusCode); // not submitted
        Assert.Equal(HttpStatusCode.OK, (await Submit(Analyst, run)).StatusCode);
        var etag = await ETagOf(Approver, run);
        Assert.Equal((HttpStatusCode)422, (await Post(Approver, run, "approve", """{"attestation":false,"reason":"Reviewed the pack"}""", etag)).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Post(Approver, run, "approve", """{"attestation":true,"reason":"x"}""", etag)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Post(Approver, run, "approve", """{"attestation":true,"reason":"Reviewed"}""", null)).StatusCode);
        Assert.Equal(0, await Audits("RUN_SIGNED_OFF", run));
        var wrongHash = await Post(Approver, run, "approve", $$"""{"attestation":true,"reason":"Reviewed","attestationTextHash":"{{new string('0', 64)}}"}""", etag);
        Assert.Equal(HttpStatusCode.Conflict, wrongHash.StatusCode);
        Assert.EndsWith("stale-signoff", (await Json(wrongHash)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_changed_exception_state_after_submission_makes_the_approval_stale_and_returns_to_preparation()
    {
        var run = await Ready(extraSql: Exc("S-{run}", "{run}", "LE-S1", false));
        Assert.Equal(HttpStatusCode.OK, (await Submit(Analyst, run)).StatusCode);
        var etag = await ETagOf(Approver, run);
        await Exec($"UPDATE exceptions SET status='IN_INVESTIGATION' WHERE id='S-{run}'"); // does not bump the sign-off ETag
        var res = await Approve(Approver, run, etag);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.EndsWith("stale-signoff", (await Json(res)).GetProperty("type").GetString());
        Assert.Equal("PREPARATION_INCOMPLETE", await Scalar<string>($"SELECT sign_off_state FROM runs WHERE id='{run}'"));
        Assert.Equal("INVALIDATED", await Scalar<string>($"SELECT a.status FROM approvals a JOIN run_signoffs s ON s.approval_id=a.id WHERE s.run_id='{run}'"));
        Assert.Equal(1, await Audits("SIGN_OFF_RETURNED", run));
        Assert.Equal(0, await Audits("RUN_SIGNED_OFF", run));

        // resubmitting works once nothing blocks, and a changed result hash is stale as well
        Assert.Equal(HttpStatusCode.OK, (await Submit(Analyst, run)).StatusCode);
        var etag2 = await ETagOf(Approver, run);
        await Exec($"UPDATE runs SET result_hash = repeat('b',64) WHERE id='{run}'");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Approve(Approver, run, etag2)).StatusCode); // the run row moved: stale ETag
        var pack = await Json(await Approver.GetAsync($"/api/v1/runs/{run}/sign-off")); // re-read: the pack lists the change and withholds APPROVE
        Assert.Contains("result", pack.GetProperty("changesSincePreparation")[0].GetString());
        Assert.False(pack.GetProperty("canSignOff").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Approve(Approver, run)).StatusCode);
        Assert.Equal(0, await Scalar<long>($"SELECT count(*) FROM run_signoffs WHERE run_id='{run}' AND state='SIGNED_OFF'"));
    }

    [Fact]
    public async Task Return_needs_a_reason_sets_returned_and_the_run_can_be_resubmitted()
    {
        var run = await Ready();
        Assert.Equal(HttpStatusCode.Conflict, (await Post(Approver, run, "return", """{"reason":"Not submitted"}""", await ETagOf(Approver, run))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Submit(Analyst, run)).StatusCode);
        var etag = await ETagOf(Approver, run);
        Assert.Equal((HttpStatusCode)422, (await Post(Approver, run, "return", """{"reason":"x"}""", etag)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Post(Approver, run, "return", """{"reason":"Investigate more"}""", null)).StatusCode);
        Assert.Equal(0, await Audits("SIGN_OFF_RETURNED", run));

        var res = await Post(Approver, run, "return", """{"reason":"Investigate the JPY break"}""", etag);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await Json(res);
        Assert.Equal("RETURNED", b.GetProperty("run").GetProperty("signOffState").GetString());
        Assert.Equal("REVIEW_REQUIRED", b.GetProperty("run").GetProperty("state").GetString());
        Assert.Equal("REJECTED", b.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal(1, await Audits("SIGN_OFF_RETURNED", run));
        Assert.Equal("Investigate the JPY break", await Scalar<string>($"SELECT reason FROM audit_events WHERE event_type='SIGN_OFF_RETURNED' AND run_id='{run}'"));

        Assert.Equal(HttpStatusCode.OK, (await Submit(Analyst, run)).StatusCode);
        Assert.Equal("APPROVAL_PENDING", await Scalar<string>($"SELECT sign_off_state FROM runs WHERE id='{run}'"));
        Assert.Equal(2, await Scalar<long>($"SELECT count(*) FROM run_signoffs WHERE run_id='{run}'"));
    }

    [Fact]
    public async Task A_named_required_approver_is_the_only_one_who_may_decide()
    {
        var other = await User("s-approver2", Role.Approver);
        var run = await Ready();
        var res = await Submit(Analyst, run, """{"requiredApproverId":"u-s-approver2"}""");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("u-s-approver2", (await Json(res)).GetProperty("run").GetProperty("requiredApprover").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Approve(Approver, run)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Approve(other, run)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_approvals_with_the_same_etag_sign_off_exactly_once()
    {
        var other = await User("s-approver3", Role.Approver);
        var run = await Ready();
        Assert.Equal(HttpStatusCode.OK, (await Submit(Analyst, run)).StatusCode);
        var etag = await ETagOf(Approver, run);
        var results = await Task.WhenAll(Approve(Approver, run, etag), Approve(other, run, etag));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict);
        Assert.Equal(1, await Audits("RUN_SIGNED_OFF", run));
    }
}
