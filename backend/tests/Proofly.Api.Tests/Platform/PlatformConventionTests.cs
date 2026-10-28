using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proofly.Api.Platform.Contract;
using Proofly.Api.Platform.Json;
using Proofly.Api.Platform.Problems;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Platform;

public class PlatformConventionTests : IDisposable
{
    private const string Base = "/api/v1/_test";
    private readonly PlatformFactory _f = new();
    private HttpClient Auth(string perms = "") => _f.Client("alice", perms);
    public void Dispose() => _f.Dispose();

    private static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    private static HttpRequestMessage Rename(string key, string ifMatch, string body, string? contentType = "application/json")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/things/T1/rename")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType ?? "text/plain"),
        };
        if (key.Length > 0) { req.Headers.Add("Idempotency-Key", key); }
        if (ifMatch.Length > 0) { req.Headers.TryAddWithoutValidation("If-Match", ifMatch); }
        return req;
    }

    private static string Key() => Guid.NewGuid().ToString();
    private const string GoodBody = """{"name":"beta","reason":"because"}""";

    // ---- problems -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("forbidden", 403)]
    [InlineData("out-of-scope", 403)]
    [InlineData("separation-of-duties", 403)]
    [InlineData("not-found", 404)]
    [InlineData("invalid-state", 409)]
    [InlineData("run-superseded", 409)]
    [InlineData("stale-signoff", 409)]
    [InlineData("idempotency-in-progress", 409)]
    [InlineData("duplicate", 409)]
    [InlineData("precondition-failed", 412)]
    [InlineData("validation", 422)]
    [InlineData("precondition-required", 428)]
    [InlineData("unauthenticated", 401)]
    public async Task Problem_exceptions_map_to_rfc9457_with_request_id(string slug, int status)
    {
        var res = await Auth().GetAsync($"{Base}/problem/{slug}");
        Assert.Equal(status, (int)res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var p = await Json(res);
        Assert.Equal($"https://proofly.example/problems/{slug}", (string?)p["type"]);
        Assert.Equal(status, (int?)p["status"]);
        Assert.Equal("test detail", (string?)p["detail"]);
        Assert.Equal(res.Headers.GetValues("X-Request-Id").Single(), (string?)p["requestId"]);
        Assert.Equal($"{Base}/problem/{slug}", (string?)p["instance"]);
        if (slug == "validation") { Assert.Equal("/x", (string?)p["errors"]![0]!["pointer"]); }
        if (status == 401) { Assert.Equal("Bearer", res.Headers.WwwAuthenticate.ToString()); }
    }

    [Fact]
    public async Task Unhandled_exception_is_500_without_leaking_internals()
    {
        var res = await Auth().GetAsync($"{Base}/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret internals", text);
        Assert.Equal("https://proofly.example/problems/internal", (string?)JsonNode.Parse(text)!["type"]);
    }

    [Fact]
    public async Task Unknown_route_gets_a_problem_body()
    {
        var res = await Auth().GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.NotNull((string?)(await Json(res))["requestId"]);
    }

    // ---- X-Request-Id ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Request_id_is_echoed()
    {
        var id = Guid.NewGuid().ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("X-Request-Id", id);
        var res = await _f.CreateClient().SendAsync(req);
        Assert.Equal(id, res.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Request_id_is_generated_when_missing()
    {
        var res = await _f.CreateClient().GetAsync("/healthz");
        Assert.True(Guid.TryParse(res.Headers.GetValues("X-Request-Id").Single(), out _));
    }

    [Fact]
    public async Task Invalid_request_id_is_rejected_with_a_generated_id()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("X-Request-Id", "not-a-uuid");
        var res = await _f.CreateClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var p = await Json(res);
        Assert.Equal("X-Request-Id", (string?)p["errors"]![0]!["pointer"]);
        Assert.True(Guid.TryParse((string?)p["requestId"], out _));
    }

    // ---- authentication / permission --------------------------------------------------------------------------

    [Fact]
    public async Task Contract_operation_without_caller_is_401()
    {
        var res = await _f.Client(user: null).GetAsync($"{Base}/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Contract_operation_requires_x_required_permission()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Auth("audit.view").GetAsync($"{Base}/secure")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Auth("run.view").GetAsync($"{Base}/secure")).StatusCode);
        Assert.Equal("forbidden", ((string?)(await Json(await Auth("audit.view").GetAsync($"{Base}/secure")))["type"])!.Split('/').Last());
    }

    [Fact]
    public async Task Non_contract_endpoints_are_authenticated_unless_anonymous_and_honour_RequirePermission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client(null).GetAsync($"{Base}/unlisted")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Auth().GetAsync($"{Base}/unlisted")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Client(null).GetAsync($"{Base}/open")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Auth().GetAsync($"{Base}/audit-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Auth("audit.view").GetAsync($"{Base}/audit-only")).StatusCode);
    }

    // ---- request validation ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=abc", "limit")]
    [InlineData("includeTotal=maybe", "includeTotal")]
    [InlineData("severity=CRITICAL,NOPE", "severity")]
    [InlineData("bogus=1", "bogus")]
    public async Task Invalid_query_is_422_with_parameter_pointer(string query, string pointer)
    {
        var res = await Auth().GetAsync($"{Base}/items?{query}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var errors = (await Json(res))["errors"]!.AsArray();
        Assert.Contains(errors, e => (string?)e!["pointer"] == pointer);
    }

    [Fact]
    public async Task Valid_list_query_is_accepted()
    {
        var res = await Auth().GetAsync($"{Base}/items?limit=5&severity=HIGH,LOW");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Body_schema_violations_are_422_with_json_pointers()
    {
        var res = await Auth().SendAsync(Rename(Key(), "\"v1\"", """{"name":"beta","reason":"x","extra":1}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains((await Json(res))["errors"]!.AsArray(), e => (string?)e!["pointer"] == "/reason");
        Assert.Equal(0, _f.Endpoints.RenameCalls);
    }

    [Fact]
    public async Task Missing_malformed_or_wrong_type_bodies_are_422()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Auth().SendAsync(Rename(Key(), "\"v1\"", ""))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Auth().SendAsync(Rename(Key(), "\"v1\"", "{nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Auth().SendAsync(Rename(Key(), "\"v1\"", GoodBody, "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Auth().SendAsync(Rename(Key(), "\"v1\"", """{"name":"a","name":"b","reason":"abc"}"""))).StatusCode);
    }

    [Fact]
    public async Task Idempotency_key_is_required_and_must_be_a_uuid()
    {
        var missing = await Auth().SendAsync(Rename("", "\"v1\"", GoodBody));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains((await Json(missing))["errors"]!.AsArray(), e => (string?)e!["pointer"] == "Idempotency-Key");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Auth().SendAsync(Rename("abc", "\"v1\"", GoodBody))).StatusCode);
    }

    // ---- ETag / If-Match -------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_strong_etag_and_If_None_Match_gives_304()
    {
        var c = Auth();
        var res = await c.GetAsync($"{Base}/things/T1");
        var etag = res.Headers.ETag!;
        Assert.False(etag.IsWeak);
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/things/T1");
        req.Headers.TryAddWithoutValidation("If-None-Match", "W/" + etag.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Missing_If_Match_is_428_and_stale_is_412_and_current_succeeds()
    {
        var c = Auth();
        var missing = await c.SendAsync(Rename(Key(), "", GoodBody));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal("precondition-required", ((string?)(await Json(missing))["type"])!.Split('/').Last());

        var stale = await c.SendAsync(Rename(Key(), "\"v99\"", GoodBody));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var weak = await c.SendAsync(Rename(Key(), "W/\"v1\"", GoodBody));
        Assert.Equal(HttpStatusCode.PreconditionFailed, weak.StatusCode); // If-Match uses strong comparison
        Assert.Equal(0, _f.Endpoints.RenameCalls);

        var ok = await c.SendAsync(Rename(Key(), "\"v1\"", GoodBody));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"v2\"", ok.Headers.ETag!.Tag);

        // Same ETag again is now stale.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await c.SendAsync(Rename(Key(), "\"v1\"", GoodBody))).StatusCode);
    }

    [Fact]
    public void ETag_helpers_distinguish_weak_and_strong()
    {
        Assert.Equal("\"abc\"", Proofly.Api.Platform.Http.ETags.Strong("abc"));
        Assert.Equal("W/\"abc\"", Proofly.Api.Platform.Http.ETags.Weak("abc"));
        Assert.False(Proofly.Api.Platform.Http.ETags.StrongMatch("W/\"a\"", "W/\"a\""));
        Assert.True(Proofly.Api.Platform.Http.ETags.WeakMatch("W/\"a\"", "\"a\""));
        Assert.Throws<ArgumentException>(() => Proofly.Api.Platform.Http.ETags.Strong("a\"b"));
    }

    // ---- Idempotency-Key -------------------------------------------------------------------------------------

    [Fact]
    public async Task Same_key_and_body_replays_the_stored_response()
    {
        var c = Auth();
        var key = Key();
        var first = await c.SendAsync(Rename(key, "\"v1\"", GoodBody));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        var firstBody = await first.Content.ReadAsStringAsync();

        // Different property order / whitespace and the now-stale If-Match: still a replay.
        var again = await c.SendAsync(Rename(key, "\"v1\"", """{ "reason": "because", "name": "beta" }"""));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(firstBody, await again.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.ETag, again.Headers.ETag);
        Assert.Equal(1, _f.Endpoints.RenameCalls);
        Assert.NotEqual(first.Headers.GetValues("X-Request-Id").Single(), again.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Same_key_with_different_body_is_422()
    {
        var c = Auth();
        var key = Key();
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Rename(key, "\"v1\"", GoodBody))).StatusCode);
        var res = await c.SendAsync(Rename(key, "\"v2\"", """{"name":"gamma","reason":"because"}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains((await Json(res))["errors"]!.AsArray(), e => (string?)e!["pointer"] == "Idempotency-Key");
        Assert.Equal(1, _f.Endpoints.RenameCalls);
    }

    [Fact]
    public async Task Keys_are_scoped_per_caller()
    {
        var key = Key();
        Assert.Equal(HttpStatusCode.OK, (await _f.Client("alice").SendAsync(Rename(key, "\"v1\"", GoodBody))).StatusCode);
        var bob = await _f.Client("bob").SendAsync(Rename(key, "\"v2\"", GoodBody));
        Assert.Equal(HttpStatusCode.OK, bob.StatusCode);
        Assert.False(bob.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Failed_requests_release_the_key_so_the_client_can_retry()
    {
        var c = Auth();
        var key = Key();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await c.SendAsync(Rename(key, "\"v9\"", GoodBody))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Rename(key, "\"v1\"", GoodBody))).StatusCode);
    }

    [Fact]
    public async Task Request_in_flight_with_same_key_is_409()
    {
        var c = Auth();
        var key = Key();
        HttpRequestMessage Slow() { var r = new HttpRequestMessage(HttpMethod.Post, $"{Base}/slow") { Content = new StringContent("{}", Encoding.UTF8, "application/json") }; r.Headers.Add("Idempotency-Key", key); return r; }

        var first = c.SendAsync(Slow());
        await _f.Endpoints.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await c.SendAsync(Slow());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("idempotency-in-progress", ((string?)(await Json(second))["type"])!.Split('/').Last());

        _f.Endpoints.SlowRelease.SetResult();
        Assert.Equal(HttpStatusCode.Accepted, (await first).StatusCode);
        var replay = await c.SendAsync(Slow());
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
    }

    // ---- paging ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cursor_paging_walks_all_items_with_opaque_cursors()
    {
        var c = Auth();
        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{Base}/items?limit=25" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var body = await Json(await c.GetAsync(url));
            seen.AddRange(body["items"]!.AsArray().Select(i => (int)i!));
            var page = body["page"]!;
            Assert.Null(page["total"]);
            cursor = (string?)page["nextCursor"];
            Assert.Equal(cursor is not null, (bool)page["hasMore"]!);
            if (cursor is not null) { Assert.DoesNotMatch("[+/=]", cursor); }
            pages++;
        } while (cursor is not null);
        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(1, 60), seen);
    }

    [Fact]
    public async Task Include_total_adds_page_total_and_invalid_cursor_is_422()
    {
        var c = Auth();
        var body = await Json(await c.GetAsync($"{Base}/items?limit=10&includeTotal=true"));
        Assert.Equal(60, (int?)body["page"]!["total"]);
        var bad = await c.GetAsync($"{Base}/items?cursor=%%%");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var bad2 = await c.GetAsync($"{Base}/items?cursor=bm90LWpzb24");
        Assert.Contains((await Json(bad2))["errors"]!.AsArray(), e => (string?)e!["pointer"] == "cursor");
    }

    // ---- JSON wire format and response validation -----------------------------------------------------------

    [Fact]
    public async Task Wire_format_uses_string_decimals_and_contract_enum_names()
    {
        var body = await Json(await Auth().GetAsync($"{Base}/wire"));
        Assert.Equal("-50.10", (string?)body["amount"]);
        Assert.Equal(JsonValueKind.String, body["amount"]!.GetValueKind());
        Assert.Equal("HIGH", (string?)body["severity"]);
        Assert.Equal("independent_ledger", (string?)body["area"]);
        Assert.Equal("audit.view", (string?)body["permission"]);
    }

    [Fact]
    public void Json_options_reject_numeric_money_and_unknown_enums()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Amount>("""{"value":12.5}""", WireJson.Options));
        Assert.Equal(12.50m, JsonSerializer.Deserialize<Amount>("""{"value":"12.50"}""", WireJson.Options)!.Value);
        Assert.Equal(Severity.High, JsonSerializer.Deserialize<Severity>("\"HIGH\"", WireJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Severity>("\"high\"", WireJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Severity>("1", WireJson.Options));
        Assert.Equal("\"12.50\"", JsonSerializer.Serialize(12.50m, WireJson.Options));
        Assert.Equal("{\"someName\":1}", JsonSerializer.Serialize(new { SomeName = 1 }, WireJson.Options));
    }

    private sealed record Amount(decimal Value);

    [Fact]
    public async Task Response_that_violates_the_contract_is_500_and_not_leaked()
    {
        var res = await Auth().GetAsync($"{Base}/bad-response");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.DoesNotContain("not-an-int", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Response_validation_can_be_switched_off()
    {
        using var f = new PlatformFactory { ValidateResponses = false };
        Assert.Equal(HttpStatusCode.OK, (await f.Client().GetAsync($"{Base}/bad-response")).StatusCode);
    }

    // ---- CORS ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preflight_from_web_dev_origin_is_allowed_with_the_convention_headers()
    {
        var req = new HttpRequestMessage(HttpMethod.Options, $"{Base}/things/T1/rename");
        req.Headers.Add("Origin", "http://localhost:5173");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "if-match,idempotency-key,x-request-id,content-type");
        var res = await _f.CreateClient().SendAsync(req);
        Assert.Equal("http://localhost:5173", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowed = string.Join(",", res.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("If-Match", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Idempotency-Key", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cors_headers_survive_on_problem_responses_and_foreign_origins_get_none()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/secure");
        req.Headers.Add("Origin", "http://localhost:5173");
        var res = await _f.Client(null).SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"));

        var foreign = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.False((await _f.CreateClient().SendAsync(foreign)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- the real contract ----------------------------------------------------------------------------------

    [Fact]
    public void Embedded_contract_loads_and_resolves_refs()
    {
        var contract = OpenApiContract.LoadEmbedded();
        Assert.Equal(54, contract.Operations.Count);
        var getRun = contract.Match("GET", "/api/v1/runs/R-1");
        Assert.Equal("getRun", getRun!.OperationId);
        Assert.Equal("run.view", getRun.RequiredPermission);
        var reprocess = contract.Match("POST", "/api/v1/runs/R-1/reprocess")!;
        Assert.True(reprocess.RequiresIfMatch);
        Assert.True(reprocess.RequiresIdempotencyKey);
        Assert.NotNull(reprocess.BodySchemaPointer);

        var doc = getRun.Document;
        Assert.Empty(doc.Validate("/components/schemas/Money", JsonNode.Parse("""{"amount":"-50.00","currency":"EUR"}""")));
        Assert.NotEmpty(doc.Validate("/components/schemas/Money", JsonNode.Parse("""{"amount":-50.0,"currency":"EUR"}""")));
        Assert.NotEmpty(doc.Validate("/components/schemas/Money", JsonNode.Parse("""{"amount":"1.1234567","currency":"EUR"}""")));
        Assert.NotEmpty(doc.Validate("/components/schemas/Money", JsonNode.Parse("""{"amount":"1.00","currency":"eur"}""")));
        Assert.Empty(doc.Validate(reprocess.BodySchemaPointer!, JsonNode.Parse("""{"reason":"late evidence"}""")));
        Assert.NotEmpty(doc.Validate(reprocess.BodySchemaPointer!, JsonNode.Parse("""{"reason":"x"}""")));
    }

    [Fact]
    public async Task Real_contract_operation_is_gated_before_the_endpoint_exists()
    {
        // GET /runs/{id} is in the contract; no endpoint is registered yet, so routing yields 404 as a problem.
        var res = await Auth("run.view").GetAsync("/api/v1/runs/R-1");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }
}
