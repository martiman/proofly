using System.Net;
using System.Text;
using System.Text.Json;
using Proofly.Api.Tests.Auth;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Runs;

/// <summary>requestRunReprocess and compareRuns. Neutral GBP/EUR/JPY data, independent of examples/synthetic-data.</summary>
public sealed class RunCommandsApiTests(AuthFixture f) : IClassFixture<AuthFixture>, IAsyncLifetime
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;
    private AuthFactory F => f.Factory;

    private static string Run(string id, string le, string date, int version, string state, string signOff = "PREPARATION_INCOMPLETE", string? supersedes = null) => $"""
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, supersedes_run_id, result_hash)
        VALUES ('{id}','{le}','{date}',{version},'rs1','{state}','{signOff}',{(supersedes is null ? "NULL" : $"'{supersedes}'")}, repeat('a',64));
        """;

    private static string Exc(string id, string run, string le, string date, string key, string severity, decimal? value, string status = "OPEN") => $"""
        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, finding_key)
        VALUES ('{id}','{run}','{le}','{date}','t','matching','VALUE_MISMATCH','{severity}','{status}',{(value is null ? "NULL" : value.ToString())},{(value is null ? "NULL" : "'JPY'")},'2026-10-30T00:00Z','{key}');
        """;

    public async Task InitializeAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded) { return; }
            var sql = """
                INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('LE-R1','Entity R1','GBP','{EUR,JPY}'), ('LE-R2','Entity R2','EUR','{}');
                INSERT INTO users (id, name, email) VALUES ('u-prep','Preparer','prep@x.test'), ('u-sign','Signer','sign@x.test') ON CONFLICT DO NOTHING;
                """
                + Run("RUN-HAPPY", "LE-R1", "2026-02-01", 1, "SIGNED_OFF", "SIGNED_OFF")
                + "INSERT INTO run_signoffs (run_id, state, prepared_by_user_id, signed_by_user_id, signed_at) VALUES ('RUN-HAPPY','SIGNED_OFF','u-prep','u-sign','2026-02-02T10:00Z');"
                + Run("RUN-NOHDR", "LE-R1", "2026-02-02", 1, "REVIEW_REQUIRED")
                + Run("RUN-STALE", "LE-R1", "2026-02-03", 1, "READY_FOR_SIGN_OFF", "READY_FOR_REVIEW")
                + Run("RUN-BUSY", "LE-R1", "2026-02-04", 1, "PROCESSING")
                + Run("RUN-OLD", "LE-R1", "2026-02-05", 1, "SUPERSEDED", "SUPERSEDED")
                + Run("RUN-NEW", "LE-R1", "2026-02-05", 2, "REVIEW_REQUIRED", "PREPARATION_INCOMPLETE", "RUN-OLD")
                + Run("RUN-SHORT", "LE-R1", "2026-02-06", 1, "REVIEW_REQUIRED")
                + Run("RUN-R2", "LE-R2", "2026-02-07", 1, "REVIEW_REQUIRED")
                + Run("RUN-IDEM", "LE-R1", "2026-02-08", 1, "REVIEW_REQUIRED")
                + Run("CMP-1", "LE-R1", "2026-03-01", 1, "SUPERSEDED", "SUPERSEDED")
                + Run("CMP-2", "LE-R1", "2026-03-01", 2, "REVIEW_REQUIRED", "PREPARATION_INCOMPLETE", "CMP-1")
                + Exc("E-1a", "CMP-1", "LE-R1", "2026-03-01", "k:same", "MEDIUM", 200m)
                + Exc("E-1b", "CMP-1", "LE-R1", "2026-03-01", "k:gone", "MEDIUM", 50m)
                + Exc("E-2a", "CMP-2", "LE-R1", "2026-03-01", "k:same", "HIGH", 300m)
                + Exc("E-2c", "CMP-2", "LE-R1", "2026-03-01", "k:new", "LOW", null)
                + """
                INSERT INTO run_control_results (run_id, control, state, rule_version, record_count, finding_count, blocking_finding_count) VALUES
                  ('CMP-1','matching','REVIEW_REQUIRED','m1',3,2,0), ('CMP-2','matching','REVIEW_REQUIRED','m1',5,2,0),
                  ('CMP-1','safeguarding','FAILED','s1',1,1,1), ('CMP-2','safeguarding','PASSED','s1',2,0,0),
                  ('CMP-1','data_completeness','PASSED','c1',10,0,0);
                INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state) VALUES
                  ('CMP-1','LE-R1','GBP',1000,900,-100,'SHORTFALL'), ('CMP-2','LE-R1','GBP',1000,1050,50,'SURPLUS'), ('CMP-2','LE-R1','EUR',10,10,0,'COVERED');
                UPDATE runs SET input_manifest='[{"deliveryId":"D1","sourceName":"s","fingerprint":"aa","state":"RECEIVED"},{"deliveryId":"D2","sourceName":"t","fingerprint":"bb","state":"RECEIVED"}]' WHERE id='CMP-1';
                UPDATE runs SET input_manifest='[{"deliveryId":"D1","sourceName":"s","fingerprint":"aa","state":"RECEIVED"},{"deliveryId":"D2","sourceName":"t","fingerprint":"cc","state":"RECEIVED"}]' WHERE id='CMP-2';
                """;
            await using var c = f.Db.CreateCommand(sql);
            await c.ExecuteNonQueryAsync();
            _seeded = true;
        }
        finally { SeedLock.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Approver => F.As(F.TokenFor(Role.Approver));
    private HttpClient Analyst => F.As(F.TokenFor(Role.Analyst));
    private HttpClient Viewer => F.As(F.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> ETagOf(HttpClient c, string id) => (await c.GetAsync($"/api/v1/runs/{id}")).Headers.ETag!.ToString();

    private static Task<HttpResponseMessage> Reprocess(HttpClient c, string id, string body, string? etag, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/runs/{id}/reprocess") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return c.SendAsync(req);
    }

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var c = f.Db.CreateCommand(sql);
        var v = await c.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    private async Task<HttpClient> ScopedTo(string key, Role role, string le)
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true, [new RoleAssignment(WireNames.ToUpperSnake(role), le)]));
        return F.As(F.Tokens.Issue("sub-" + key));
    }

    [Fact]
    public async Task Reprocess_creates_the_next_version_audits_in_the_same_transaction_and_keeps_the_signed_run()
    {
        var c = Approver;
        var etag = await ETagOf(c, "RUN-HAPPY");
        var res = await Reprocess(c, "RUN-HAPPY", """{"reason":"Late corrected evidence arrived"}""", etag);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var run = await Json(res);
        Assert.Equal(2, run.GetProperty("version").GetInt32());
        Assert.Equal("RUN-HAPPY", run.GetProperty("supersedesRunId").GetString());
        Assert.Equal("PROCESSING", run.GetProperty("state").GetString());
        Assert.Equal("PREPARATION_INCOMPLETE", run.GetProperty("signOffState").GetString());
        Assert.NotNull(res.Headers.ETag);

        var old = await Json(await c.GetAsync("/api/v1/runs/RUN-HAPPY"));
        Assert.Equal("SUPERSEDED", old.GetProperty("state").GetString());
        Assert.Equal(run.GetProperty("id").GetString(), old.GetProperty("supersededByRunId").GetString());
        Assert.Equal("u-sign", old.GetProperty("signedOffBy").GetProperty("id").GetString()); // the sign-off record is preserved

        Assert.Equal(1, await Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED' AND subject_id='RUN-HAPPY' AND reason='Late corrected evidence arrived' AND actor_type='USER'"));
        Assert.Equal(1, await Scalar<long>($"SELECT count(*) FROM jobs WHERE type='run.process' AND run_id='{run.GetProperty("id").GetString()}'"));
    }

    [Fact]
    public async Task Reprocess_is_idempotent_for_the_same_key()
    {
        var c = Approver;
        var etag = await ETagOf(c, "RUN-IDEM");
        var key = Guid.NewGuid().ToString();
        var first = await Reprocess(c, "RUN-IDEM", """{"reason":"Retry safely"}""", etag, key);
        var second = await Reprocess(c, "RUN-IDEM", """{"reason":"Retry safely"}""", etag, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal((await Json(first)).GetProperty("id").GetString(), (await Json(second)).GetProperty("id").GetString());
        Assert.Equal(2, await Scalar<long>("SELECT count(*) FROM runs WHERE legal_entity_id='LE-R1' AND business_date='2026-02-08'")); // v1 and one v2
        Assert.Equal(1, await Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED' AND subject_id='RUN-IDEM'"));
    }

    [Fact]
    public async Task Reprocess_requires_if_match_and_a_current_etag()
    {
        var c = Approver;
        Assert.Equal((HttpStatusCode)428, (await Reprocess(c, "RUN-NOHDR", """{"reason":"No etag"}""", null)).StatusCode);
        var stale = await Reprocess(c, "RUN-STALE", """{"reason":"Stale etag"}""", "\"v999\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(0, await Scalar<long>("SELECT count(*) FROM audit_events WHERE subject_id IN ('RUN-NOHDR','RUN-STALE')"));
        Assert.Equal("READY_FOR_SIGN_OFF", await Scalar<string>("SELECT state FROM runs WHERE id='RUN-STALE'"));
    }

    [Fact]
    public async Task Reprocess_is_refused_without_permission_outside_scope_and_for_the_wrong_state()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Reprocess(Analyst, "RUN-SHORT", """{"reason":"Not allowed"}""", await ETagOf(Analyst, "RUN-SHORT"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reprocess(Viewer, "RUN-SHORT", """{"reason":"Not allowed"}""", await ETagOf(Viewer, "RUN-SHORT"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reprocess(Approver, "RUN-NONE", """{"reason":"No such run"}""", "\"v1\"")).StatusCode);
        var other = await ScopedTo("reproc-other", Role.Approver, "LE-R2");
        Assert.Equal(HttpStatusCode.NotFound, (await Reprocess(other, "RUN-SHORT", """{"reason":"Out of scope"}""", "\"v1\"")).StatusCode);

        var superseded = await Reprocess(Approver, "RUN-OLD", """{"reason":"Already superseded"}""", await ETagOf(Approver, "RUN-OLD"));
        Assert.Equal(HttpStatusCode.Conflict, superseded.StatusCode);
        Assert.EndsWith("/run-superseded", (await Json(superseded)).GetProperty("type").GetString());
        var busy = await Reprocess(Approver, "RUN-BUSY", """{"reason":"Still processing"}""", await ETagOf(Approver, "RUN-BUSY"));
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.EndsWith("/invalid-state", (await Json(busy)).GetProperty("type").GetString());

        var tooShort = await Reprocess(Approver, "RUN-SHORT", """{"reason":"x"}""", await ETagOf(Approver, "RUN-SHORT"));
        Assert.Equal((HttpStatusCode)422, tooShort.StatusCode);
        Assert.Equal(0, await Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED' AND subject_id IN ('RUN-SHORT','RUN-OLD','RUN-BUSY')"));
        Assert.Equal("REVIEW_REQUIRED", await Scalar<string>("SELECT state FROM runs WHERE id='RUN-SHORT'"));
    }

    [Fact]
    public async Task Compare_shows_new_resolved_changed_deliveries_areas_and_safeguarding_changes()
    {
        var res = await Viewer.GetAsync("/api/v1/runs/CMP-2/compare?against=CMP-1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Json(res);
        Assert.Equal("CMP-2", body.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal("CMP-1", body.GetProperty("against").GetProperty("id").GetString());
        Assert.Equal(["E-2c"], body.GetProperty("newExceptionIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["E-1b"], body.GetProperty("resolvedExceptionIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["D2"], body.GetProperty("changedDeliveries").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var areas = body.GetProperty("controlAreas").EnumerateArray().ToDictionary(a => a.GetProperty("area").GetString()!);
        Assert.Equal(["matching", "safeguarding"], areas.Keys.Order().ToArray());
        Assert.Equal(2, areas["matching"].GetProperty("recordCountDelta").GetInt32());
        Assert.Equal("PASSED", areas["safeguarding"].GetProperty("state").GetString());
        Assert.Equal("FAILED", areas["safeguarding"].GetProperty("againstState").GetString());
        var drivers = body.GetProperty("safeguardingDelta").EnumerateArray().ToList();
        Assert.Contains(drivers, d => d.GetProperty("label").GetString() == "Safeguarding position" && d.GetProperty("currency").GetString() == "GBP" && d.GetProperty("change").GetProperty("amount").GetString() == "150.00");
        Assert.DoesNotContain(drivers, d => d.GetProperty("currency").GetString() == "EUR" && d.GetProperty("label").GetString() == "Safeguarding position");
    }

    [Fact]
    public async Task Compare_validates_the_comparison_target_and_scope()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/CMP-2/compare?against=NOPE")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/NOPE/compare?against=CMP-1")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Viewer.GetAsync("/api/v1/runs/CMP-2/compare?against=RUN-R2")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await Viewer.GetAsync("/api/v1/runs/CMP-2/compare")).StatusCode);
        var other = await ScopedTo("cmp-other", Role.Viewer, "LE-R2");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/runs/CMP-2/compare?against=CMP-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(null).GetAsync("/api/v1/runs/CMP-2/compare?against=CMP-1")).StatusCode);
    }
}
