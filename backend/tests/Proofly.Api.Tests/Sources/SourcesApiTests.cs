using System.Net;
using System.Text.Json;
using Npgsql;
using Proofly.Api.Tests.Auth;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Sources;

public sealed class SourcesApiTests(AuthFixture f) : IClassFixture<AuthFixture>, IAsyncLifetime
{
    private AuthFactory F => f.Factory;

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded; // the fixture (schema) is created once per process for this class

    public async Task InitializeAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seeded) { return; }
            await Seed();
            _seeded = true;
        }
        finally { SeedLock.Release(); }
    }

    private async Task Seed()
    {
        await using var c = f.Db.CreateCommand(SourceSeed.Sql);
        await c.ExecuteNonQueryAsync();
        await using var a = f.Db.CreateCommand("""
            INSERT INTO audit_events (event_type, actor_type, actor_id, subject_kind, subject_id, summary, after, chain_id, chain_seq, hash)
            VALUES ('DELIVERY_RECEIVED','SYSTEM',NULL,'DELIVERY','D1','registered','{"state":"RECEIVED"}','*',1,repeat('0',64)),
                   ('DELIVERY_RECEIVED','SYSTEM',NULL,'DELIVERY','D1','ingested','{"state":"QUALIFIED","acceptedRecords":3}','*',2,repeat('1',64))
            """);
        await a.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Viewer => F.As(F.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<HttpClient> ScopedTo(string key, Role role, string le)
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true, [new RoleAssignment(WireNames.ToUpperSnake(role), le)]));
        return F.As(F.Tokens.Issue("sub-" + key));
    }

    [Fact]
    public async Task Run_deliveries_validate_against_the_contract()
    {
        var res = await Viewer.GetAsync("/api/v1/runs/RUN-A/deliveries");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var items = (await Json(res)).GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var d1 = items.EnumerateArray().Single(i => i.GetProperty("deliveryId").GetString() == "D1");
        Assert.Equal(["EUR 20.123456", "GBP 150.500000", "JPY 3000.000000"], d1.GetProperty("declaredTotals").EnumerateArray().Select(m => m.GetProperty("currency").GetString() + " " + m.GetProperty("amount").GetString()).ToArray());
        Assert.Equal(5, d1.GetProperty("actualRecordCount").GetInt32());
        var d2 = items.EnumerateArray().Single(i => i.GetProperty("deliveryId").GetString() == "D2");
        Assert.Equal("D1", d2.GetProperty("correctionOfDeliveryId").GetString());
    }

    [Fact]
    public async Task Detail_has_etag_checks_and_actions_and_honours_if_none_match()
    {
        var res = await Viewer.GetAsync("/api/v1/deliveries/D1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var etag = res.Headers.ETag!.ToString();
        Assert.Equal("\"v1\"", etag);
        var j = await Json(res);
        Assert.Equal(64, j.GetProperty("fingerprint").GetProperty("value").GetString()!.Length);
        Assert.Equal("CONTROL_TOTALS", j.GetProperty("checks").EnumerateArray().Last().GetProperty("check").GetString());
        Assert.Equal("FAIL", j.GetProperty("checks").EnumerateArray().Last().GetProperty("result").GetString()); // EUR declared, never measured
        Assert.Empty(j.GetProperty("allowedActions").EnumerateArray()); // viewer has neither source.retry nor source.supersede

        var cached = new HttpRequestMessage(HttpMethod.Get, "/api/v1/deliveries/D1");
        cached.Headers.IfNoneMatch.ParseAdd(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await Viewer.SendAsync(cached)).StatusCode);

        var approver = await Json(await F.As(F.TokenFor(Role.Approver)).GetAsync("/api/v1/deliveries/D1"));
        Assert.Equal(["MARK_SUPERSEDED"], approver.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!).ToArray());

        var dup = await Json(await Viewer.GetAsync("/api/v1/deliveries/D2"));
        var checks = dup.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("check").GetString()!);
        Assert.Equal("FAIL", checks["DUPLICATE"].GetProperty("result").GetString());
        Assert.Equal("D1", checks["DUPLICATE"].GetProperty("relatedDeliveryId").GetString());
    }

    [Fact]
    public async Task Records_page_and_filter()
    {
        var p1 = await Json(await Viewer.GetAsync("/api/v1/deliveries/D1/records?limit=2"));
        Assert.Equal(["s1", "s2"], p1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sourceRowId").GetString()!).ToArray());
        Assert.True(p1.GetProperty("page").GetProperty("hasMore").GetBoolean());
        // includeTotal is not declared on this operation in the contract (gap), so no total is requested here.
        var cursor = Uri.EscapeDataString(p1.GetProperty("page").GetProperty("nextCursor").GetString()!);
        var p2 = await Json(await Viewer.GetAsync($"/api/v1/deliveries/D1/records?limit=2&cursor={cursor}"));
        Assert.Equal(["s3", "s4"], p2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sourceRowId").GetString()!).ToArray());
        var rej = await Json(await Viewer.GetAsync("/api/v1/deliveries/D1/records?accepted=false"));
        Assert.Equal(2, rej.GetProperty("items").GetArrayLength());
        Assert.False(rej.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var none = await Json(await Viewer.GetAsync("/api/v1/deliveries/D2/records"));
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/deliveries/D1/records?cursor=garbage")).StatusCode);
    }

    [Fact]
    public async Task Record_preview_and_history()
    {
        var rec = await Json(await Viewer.GetAsync("/api/v1/deliveries/D1/records?limit=1"));
        var id = rec.GetProperty("items")[0].GetProperty("recordId").GetString()!;
        var res = await Viewer.GetAsync("/api/v1/source-records/" + Uri.EscapeDataString(id));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("100.000000", (await Json(res)).GetProperty("fields").GetProperty("amount").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/source-records/nope:1")).StatusCode);

        var h = (await Json(await Viewer.GetAsync("/api/v1/deliveries/D1/processing-history"))).GetProperty("items");
        Assert.Equal(["RECEIVED", "ACCEPTED"], h.EnumerateArray().Select(s => s.GetProperty("stage").GetString()!).ToArray());
        Assert.Equal("SYSTEM", h[0].GetProperty("actor").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Out_of_scope_is_404_missing_permission_403_and_anonymous_401()
    {
        var b = await ScopedTo("src-b", Role.Analyst, "LE-B");
        foreach (var url in new[] { "/api/v1/runs/RUN-A/deliveries", "/api/v1/deliveries/D1", "/api/v1/deliveries/D1/records", "/api/v1/deliveries/D1/processing-history" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(url)).StatusCode);
        }
        var rec = await Json(await Viewer.GetAsync("/api/v1/deliveries/D1/records?limit=1"));
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/v1/source-records/" + Uri.EscapeDataString(rec.GetProperty("items")[0].GetProperty("recordId").GetString()!))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/v1/deliveries/D3")).StatusCode);

        var noPerm = F.As(F.TokenFor(Role.ConfigAdmin));
        Assert.Equal(HttpStatusCode.Forbidden, (await noPerm.GetAsync("/api/v1/deliveries/D1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(null).GetAsync("/api/v1/deliveries/D1")).StatusCode);

        var mixed = await ScopedTo("src-mixed", Role.Viewer, "LE-A");
        Assert.Equal(HttpStatusCode.OK, (await mixed.GetAsync("/api/v1/deliveries/D1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/deliveries/UNKNOWN")).StatusCode);
    }
}
