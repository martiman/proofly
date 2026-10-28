using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (9) CORS and (10) response headers.</summary>
public sealed class HttpHardeningTests : IClassFixture<SeededDb>, IAsyncLifetime
{
    private const string Allowed = "https://proofly.sec-test.example";
    private readonly SeededDb _db;
    private SecFactory _f = null!;

    public HttpHardeningTests(SeededDb db) => _db = db;
    public async Task InitializeAsync()
    {
        _f = new SecFactory(_db, settings: new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = Allowed, ["Cors:AllowedOrigins:1"] = "https://second.sec-test.example" });
        _ = _f.Server;
        await Task.CompletedTask;
    }
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private async Task<HttpResponseMessage> Get(string origin, string path = "/api/v1/runs", Role role = Role.Viewer)
    {
        var c = _f.As(role);
        c.DefaultRequestHeaders.Add("Origin", origin);
        return await c.GetAsync(path);
    }

    private static string? Acao(HttpResponseMessage r) => r.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;

    [Fact]
    public async Task Configured_origins_are_allowed_and_echoed_exactly()
    {
        Assert.Equal(Allowed, Acao(await Get(Allowed)));
        Assert.Equal("https://second.sec-test.example", Acao(await Get("https://second.sec-test.example")));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://proofly.sec-test.example")]            // wrong scheme
    [InlineData("https://proofly.sec-test.example:8443")]      // wrong port
    [InlineData("https://proofly.sec-test.example.evil.example")]
    [InlineData("https://sub.proofly.sec-test.example")]
    [InlineData("null")]
    [InlineData("http://localhost:5173")]                      // the development default is replaced, not added, when origins are configured
    public async Task Other_origins_get_no_cors_headers_on_requests_and_preflights(string origin)
    {
        var res = await Get(origin);
        Assert.Null(Acao(res));
        Assert.False(res.Headers.Contains("Access-Control-Allow-Credentials"));

        var pre = new HttpRequestMessage(HttpMethod.Options, "/api/v1/runs");
        pre.Headers.Add("Origin", origin);
        pre.Headers.Add("Access-Control-Request-Method", "GET");
        pre.Headers.Add("Access-Control-Request-Headers", "authorization");
        var preRes = await _f.CreateClient().SendAsync(pre);
        Assert.Null(Acao(preRes));
    }

    [Fact]
    public async Task Preflight_from_an_allowed_origin_advertises_only_the_documented_methods_and_headers_and_never_credentials()
    {
        var pre = new HttpRequestMessage(HttpMethod.Options, "/api/v1/runs");
        pre.Headers.Add("Origin", Allowed);
        pre.Headers.Add("Access-Control-Request-Method", "POST");
        pre.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");
        var res = await _f.CreateClient().SendAsync(pre);
        Assert.Equal(Allowed, Acao(res));
        Assert.DoesNotContain("*", res.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.False(res.Headers.Contains("Access-Control-Allow-Credentials"));
        var methods = string.Join(',', res.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.DoesNotContain("TRACE", methods);
        Assert.DoesNotContain("CONNECT", methods);

        var bad = new HttpRequestMessage(HttpMethod.Options, "/api/v1/runs");
        bad.Headers.Add("Origin", Allowed);
        bad.Headers.Add("Access-Control-Request-Method", "POST");
        bad.Headers.Add("Access-Control-Request-Headers", "x-forwarded-user");
        var badRes = await _f.CreateClient().SendAsync(bad);
        var advertised = string.Join(',', badRes.Headers.TryGetValues("Access-Control-Allow-Headers", out var h) ? h : []);
        Assert.DoesNotContain("x-forwarded-user", advertised, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", advertised);
    }

    [Fact]
    public async Task Error_responses_do_not_widen_cors_for_foreign_origins()
    {
        Assert.Null(Acao(await Get("https://evil.example", "/api/v1/runs/nope")));
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        var unauth = await c.GetAsync("/api/v1/runs");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.Null(Acao(unauth));
    }

    [Fact]
    public async Task Without_configuration_only_the_development_origins_are_allowed()
    {
        await using var f = new SecFactory(_db);
        _ = f.Server;
        var c = f.As(Role.Viewer);
        c.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Null(Acao(await c.GetAsync("/api/v1/runs")));
    }

    // ---- (10) response headers ----

    public static TheoryData<string, bool> Paths => new()
    {
        { "/api/v1/runs", true }, { "/api/v1/me", true }, { "/api/v1/runs/RUN-SECA", true }, { "/api/v1/runs/nope", true },
        { "/api/v1/audit-events", true }, { "/api/v1/settings", true },
    };

    [Theory, MemberData(nameof(Paths))]
    public async Task Api_responses_with_data_are_not_cacheable_and_not_sniffable(string path, bool _)
    {
        var res = await _f.As(Role.Approver).GetAsync(path);
        Assert.True(res.Headers.CacheControl?.NoStore == true, $"{path} -> Cache-Control: {res.Headers.CacheControl}");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Error_and_unauthenticated_responses_are_not_cacheable_either()
    {
        foreach (var res in new[] { await _f.As((string?)null).GetAsync("/api/v1/runs"), await _f.As(Role.Viewer).GetAsync("/api/v1/runs/nope"), await _f.As(Role.Viewer).GetAsync("/api/v1/settings/changes") })
        {
            Assert.True(res.Headers.CacheControl?.NoStore == true, $"{res.StatusCode}: Cache-Control: {res.Headers.CacheControl}");
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }

    [Fact]
    public async Task No_server_or_framework_version_header_is_sent()
    {
        foreach (var res in new[] { await _f.As(Role.Viewer).GetAsync("/api/v1/runs"), await _f.As((string?)null).GetAsync("/api/v1/me"), await _f.CreateClient().GetAsync("/healthz"), await _f.CreateClient().GetAsync("/nope") })
        {
            foreach (var h in new[] { "Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version" })
            {
                Assert.False(res.Headers.Contains(h), $"{h}: {string.Join(',', res.Headers.TryGetValues(h, out var v) ? v : [])}");
            }
        }
    }

    [Fact]
    public void Kestrel_is_configured_not_to_send_a_Server_header()
    {
        // The in-memory test server has no Kestrel; the real host does, so assert the option the real host will use.
        Assert.False(_f.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.AddServerHeader);
    }

    [Fact]
    public async Task Problem_responses_hide_internals()
    {
        var res = await _f.As(Role.Viewer).GetAsync("/api/v1/runs/nope");
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        foreach (var leak in new[] { "Exception", "StackTrace", "   at Proofly", "Npgsql", "SELECT ", "/Users/", "Microsoft." }) { Assert.DoesNotContain(leak, body); }
    }
}
