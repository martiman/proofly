using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Proofly.Api.Tests.Auth;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Sources;

/// <summary>retryDeliveryIngestion and markDeliverySuperseded. Neutral GBP/EUR/JPY data, independent of examples/synthetic-data.</summary>
public sealed class SourceCommandsApiTests(AuthFixture f) : IClassFixture<AuthFixture>, IAsyncLifetime
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;
    private AuthFactory F => f.Factory;

    private static string Fp(string s) => $"encode(sha256('{s}'::bytea),'hex')";

    private static string Delivery(string id, string le, string source, string state, bool ingested, string date = "2026-03-02") => $"""
        INSERT INTO source_deliveries (id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state, ingested_at, received_at)
        VALUES ('{id}','{le}','{source}','{date}','1.0','{id}.csv',{Fp(id)},'{state}',{(ingested ? "now()" : "NULL")}, now());
        """;

    public async Task InitializeAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded) { return; }
            var sql = """
                INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-A','Entity A','GBP'), ('LE-B','Entity B','EUR');
                """
                + Delivery("R1", "LE-A", "banking_transactions", "REJECTED", false)
                + Delivery("R2", "LE-A", "banking_transactions", "REJECTED", false, "2026-03-03")
                + Delivery("R3", "LE-A", "banking_transactions", "REJECTED", false, "2026-03-04")
                + Delivery("R4", "LE-A", "banking_transactions", "REJECTED", false, "2026-03-05")
                + Delivery("R5", "LE-A", "banking_transactions", "REJECTED", false, "2026-03-06")
                + Delivery("R6", "LE-A", "banking_transactions", "REJECTED", false, "2026-03-07")
                + Delivery("RB", "LE-B", "banking_transactions", "REJECTED", false, "2026-03-08")
                + Delivery("Q1", "LE-A", "transfer_instructions", "QUALIFIED", true)
                + Delivery("Q1C", "LE-A", "transfer_instructions", "RECEIVED", true)
                + Delivery("Q2", "LE-A", "transfer_instructions", "QUALIFIED", true, "2026-03-03")
                + Delivery("Q2C", "LE-A", "transfer_instructions", "RECEIVED", true, "2026-03-03")
                + Delivery("Q2X", "LE-A", "card_transactions", "RECEIVED", true, "2026-03-03")
                + Delivery("Q2R", "LE-A", "transfer_instructions", "REJECTED", false, "2026-03-03")
                + Delivery("Q3", "LE-A", "transfer_instructions", "QUALIFIED", true, "2026-03-04")
                + Delivery("Q3C", "LE-A", "transfer_instructions", "RECEIVED", true, "2026-03-04")
                + Delivery("Q4", "LE-A", "transfer_instructions", "QUALIFIED", true, "2026-03-05")
                + Delivery("Q4C", "LE-A", "transfer_instructions", "RECEIVED", true, "2026-03-05")
                + Delivery("Q4B", "LE-B", "transfer_instructions", "RECEIVED", true, "2026-03-05")
                + Delivery("Q5", "LE-A", "transfer_instructions", "QUALIFIED", true, "2026-03-06")
                + Delivery("Q5C", "LE-A", "transfer_instructions", "RECEIVED", true, "2026-03-06")
                + """
                INSERT INTO delivery_rejections (delivery_id, reason_code, reason) VALUES ('R2','DUPLICATE_DELIVERY','Same content already delivered as Q1.'), ('R1','SCHEMA_MISMATCH','first attempt');
                """;
            await using var c = f.Db.CreateCommand(sql);
            await c.ExecuteNonQueryAsync();
            _seeded = true;
        }
        finally { SeedLock.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Analyst => F.As(F.TokenFor(Role.Analyst));
    private HttpClient Approver => F.As(F.TokenFor(Role.Approver));
    private HttpClient Viewer => F.As(F.TokenFor(Role.Viewer));

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> ETagOf(HttpClient c, string id) => (await c.GetAsync($"/api/v1/deliveries/{id}")).Headers.ETag!.ToString();

    private static Task<HttpResponseMessage> Post(HttpClient c, string id, string op, string body, string? etag, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/deliveries/{id}/{op}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return c.SendAsync(req);
    }

    private static string Retry(string reason = "Source corrected the file name") => JsonSerializer.Serialize(new { reason });
    private static string Supersede(string by, string reason = "Correction delivered") => JsonSerializer.Serialize(new { supersededByDeliveryId = by, reason });

    private async Task<long> Count(string sql, params (string, object)[] p)
    {
        await using var c = f.Db.CreateCommand(sql);
        foreach (var (n, v) in p) { c.Parameters.AddWithValue(n, v); }
        return (long)(await c.ExecuteScalarAsync())!;
    }

    private Task<long> Audits(string id, string type) => Count("SELECT count(*) FROM audit_events WHERE subject_id=@i AND event_type=@t", ("i", id), ("t", type));
    private Task<long> Jobs(string id) => Count("SELECT count(*) FROM jobs WHERE type='delivery.ingest' AND delivery_id=@i", ("i", id));

    [Fact]
    public async Task Retry_of_a_rejected_delivery_reopens_it_enqueues_a_job_and_audits_with_the_reason()
    {
        var etag = await ETagOf(Analyst, "R1");
        var res = await Post(Analyst, "R1", "retry-ingestion", Retry(), etag);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var j = await Json(res);
        Assert.Equal("RECEIVED", j.GetProperty("state").GetString());
        Assert.NotEqual(etag, res.Headers.ETag!.ToString());
        Assert.Equal(1, await Audits("R1", "DELIVERY_INGESTION_RETRIED"));
        Assert.Equal(1, await Jobs("R1"));
        // The rejection rows of the earlier attempt are kept as history but no longer count for the delivery's checks.
        Assert.Equal(1, await Count("SELECT count(*) FROM delivery_rejections WHERE delivery_id='R1' AND superseded_by_retry"));
        Assert.Equal(0, await Count("SELECT count(*) FROM delivery_rejections WHERE delivery_id='R1' AND NOT superseded_by_retry"));
        await using var c = f.Db.CreateCommand("SELECT reason, actor_id, after->>'state' FROM audit_events WHERE subject_id='R1' AND event_type='DELIVERY_INGESTION_RETRIED'");
        await using var r = await c.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("Source corrected the file name", r.GetString(0));
        Assert.Equal("RECEIVED", r.GetString(2));
        Assert.NotNull(r.GetString(1));
        await r.CloseAsync();

        var history = (await Json(await Viewer.GetAsync("/api/v1/deliveries/R1/processing-history"))).GetProperty("items");
        Assert.Contains(history.EnumerateArray(), s => s.GetProperty("stage").GetString() == "RETRIED");
    }

    [Fact]
    public async Task A_second_retry_enqueues_a_job_with_a_new_dedupe_key()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Post(Analyst, "R6", "retry-ingestion", Retry(), await ETagOf(Analyst, "R6"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(Analyst, "R6", "retry-ingestion", Retry("Retry once more"), await ETagOf(Analyst, "R6"))).StatusCode);
        Assert.Equal(2, await Jobs("R6"));
        Assert.Equal(2, await Count("SELECT count(DISTINCT dedupe_key) FROM jobs WHERE dedupe_key LIKE 'delivery.ingest:R6:retry:%'"));
        Assert.Equal(2, await Audits("R6", "DELIVERY_INGESTION_RETRIED"));
    }

    [Fact]
    public async Task Idempotent_replay_returns_the_stored_response_without_a_second_effect()
    {
        var key = Guid.NewGuid().ToString();
        var etag = await ETagOf(Analyst, "R3");
        var first = await Post(Analyst, "R3", "retry-ingestion", Retry(), etag, key);
        var second = await Post(Analyst, "R3", "retry-ingestion", Retry(), etag, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, await Audits("R3", "DELIVERY_INGESTION_RETRIED"));
        Assert.Equal(1, await Jobs("R3"));
    }

    [Fact]
    public async Task Retry_needs_if_match_428_and_a_current_one_412_and_writes_nothing_when_it_fails()
    {
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Post(Analyst, "R4", "retry-ingestion", Retry(), null)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Post(Analyst, "R4", "retry-ingestion", Retry(), "\"v99\"")).StatusCode);
        Assert.Equal(0, await Audits("R4", "DELIVERY_INGESTION_RETRIED"));
        Assert.Equal(0, await Jobs("R4"));
        Assert.Equal("REJECTED", (await Json(await Viewer.GetAsync("/api/v1/deliveries/R4"))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Retry_is_rejected_for_a_duplicate_or_a_delivery_in_the_wrong_state_with_409()
    {
        var dup = await Post(Analyst, "R2", "retry-ingestion", Retry(), await ETagOf(Analyst, "R2"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("invalid-state", (await Json(dup)).GetProperty("type").GetString()!.Split('/').Last());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(Analyst, "Q1", "retry-ingestion", Retry(), await ETagOf(Analyst, "Q1"))).StatusCode);
        Assert.Equal(0, await Audits("R2", "DELIVERY_INGESTION_RETRIED") + await Audits("Q1", "DELIVERY_INGESTION_RETRIED"));
    }

    [Fact]
    public async Task Reason_is_mandatory_422()
    {
        var etag = await ETagOf(Analyst, "R5");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Analyst, "R5", "retry-ingestion", """{"reason":"ab"}""", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Analyst, "R5", "retry-ingestion", "{}", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Analyst, "R5", "retry-ingestion", Retry("   "), etag)).StatusCode);
        Assert.Equal(0, await Audits("R5", "DELIVERY_INGESTION_RETRIED"));
    }

    [Fact]
    public async Task Permission_scope_and_anonymous_checks_for_both_commands()
    {
        var etag = await ETagOf(Analyst, "RB");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Viewer, "RB", "retry-ingestion", Retry(), etag)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(Analyst, "Q4", "mark-superseded", Supersede("Q4C"), await ETagOf(Analyst, "Q4"))).StatusCode); // analyst lacks source.supersede
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(F.As(null), "RB", "retry-ingestion", Retry(), etag)).StatusCode);

        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-a-only", "a-only", "a-only@x.test", "sub-a-only", true, [new RoleAssignment("ANALYST", "LE-A")]));
        var aOnly = F.As(F.Tokens.Issue("sub-a-only"));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(aOnly, "RB", "retry-ingestion", Retry(), etag)).StatusCode);
        Assert.Equal(0, await Audits("RB", "DELIVERY_INGESTION_RETRIED"));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(Analyst, "NOPE", "retry-ingestion", Retry(), "\"v1\"")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_retries_with_the_same_etag_let_exactly_one_win()
    {
        var etag = await ETagOf(Analyst, "RB");
        var tasks = Enumerable.Range(0, 4).Select(_ => Post(Analyst, "RB", "retry-ingestion", Retry(), etag)).ToArray();
        var codes = (await Task.WhenAll(tasks)).Select(r => r.StatusCode).ToList();
        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.Accepted));
        Assert.All(codes.Where(c => c != HttpStatusCode.Accepted), c => Assert.Equal(HttpStatusCode.PreconditionFailed, c));
        Assert.Equal(1, await Audits("RB", "DELIVERY_INGESTION_RETRIED"));
        Assert.Equal(1, await Jobs("RB"));
    }

    [Fact]
    public async Task Supersede_links_the_correction_keeps_the_original_and_audits()
    {
        var etag = await ETagOf(Approver, "Q1");
        var res = await Post(Approver, "Q1", "mark-superseded", Supersede("Q1C"), etag);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var j = await Json(res);
        Assert.Equal("SUPERSEDED", j.GetProperty("state").GetString());
        Assert.Equal("Q1C", j.GetProperty("supersededByDeliveryId").GetString());
        Assert.NotEqual(etag, res.Headers.ETag!.ToString());
        Assert.Empty(j.GetProperty("allowedActions").EnumerateArray());
        Assert.Equal(1, await Count("SELECT count(*) FROM source_deliveries WHERE id='Q1' AND state='SUPERSEDED'"));
        Assert.Equal("Q1", (await Json(await Viewer.GetAsync("/api/v1/deliveries/Q1C"))).GetProperty("correctionOfDeliveryId").GetString());
        Assert.Equal(1, await Audits("Q1", "DELIVERY_SUPERSEDED"));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_events WHERE subject_id='Q1' AND event_type='DELIVERY_SUPERSEDED' AND reason='Correction delivered' AND after->>'supersededBy'='Q1C'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM source_deliveries WHERE id='Q1' AND fingerprint_sha256 = encode(sha256('Q1'::bytea),'hex')")); // original untouched

        // Superseding again is a wrong-state conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(Approver, "Q1", "mark-superseded", Supersede("Q1C"), await ETagOf(Approver, "Q1"))).StatusCode);
    }

    [Fact]
    public async Task Supersede_validates_the_correcting_delivery_and_the_reason()
    {
        var etag = await ETagOf(Approver, "Q2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Approver, "Q2", "mark-superseded", Supersede("Q2"), etag)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Approver, "Q2", "mark-superseded", Supersede("MISSING"), etag)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Approver, "Q2", "mark-superseded", Supersede("Q2X"), etag)).StatusCode); // other source
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Approver, "Q2", "mark-superseded", Supersede("Q2C", "no"), etag)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Approver, "Q2", "mark-superseded", """{"reason":"valid reason"}""", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(Approver, "Q2", "mark-superseded", Supersede("Q2R"), etag)).StatusCode); // rejected correction
        Assert.Equal(0, await Audits("Q2", "DELIVERY_SUPERSEDED"));
        Assert.Equal("QUALIFIED", (await Json(await Viewer.GetAsync("/api/v1/deliveries/Q2"))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Supersede_needs_if_match_and_rejects_a_stale_one()
    {
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Post(Approver, "Q3", "mark-superseded", Supersede("Q3C"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Post(Approver, "Q3", "mark-superseded", Supersede("Q3C"), "\"v77\"")).StatusCode);
        Assert.Equal(0, await Audits("Q3", "DELIVERY_SUPERSEDED"));
    }

    [Fact]
    public async Task Supersede_by_a_delivery_of_another_legal_entity_is_422_and_concurrent_calls_let_one_win()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Approver, "Q4", "mark-superseded", Supersede("Q4B"), await ETagOf(Approver, "Q4"))).StatusCode);
        var etag = await ETagOf(Approver, "Q5");
        var codes = (await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Post(Approver, "Q5", "mark-superseded", Supersede("Q5C"), etag)))).Select(r => r.StatusCode).ToList();
        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(1, await Audits("Q5", "DELIVERY_SUPERSEDED"));
    }
}
