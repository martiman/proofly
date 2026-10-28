using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Api.Observability;

namespace Proofly.Api.Tests.Observability;

public class ObservabilityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class Fake(string name, bool ok, string? detail = null) : IReadinessCheck
    {
        public string Name => name;
        public Task<ReadinessResult> CheckAsync(CancellationToken ct) => Task.FromResult(new ReadinessResult(name, ok, detail));
    }

    private HttpClient Client(params IReadinessCheck[] checks) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:Proofly", "Host=localhost;Port=1;Username=x;Database=x");
        b.ConfigureServices(s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IReadinessCheck)).ToList()) { s.Remove(d); }
            foreach (var c in checks) { s.AddSingleton(c); }
        });
    }).CreateClient();

    [Fact]
    public async Task Readyz_is_200_when_all_checks_pass()
    {
        var res = await Client(new Fake("database", true), new Fake("evidence", true)).GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_is_503_and_names_the_failing_check()
    {
        var res = await Client(new Fake("database", true), new Fake("migrations", false, "1 pending migration(s): 0009_x")).GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("not_ready", body);
        Assert.Contains("0009_x", body);
    }

    [Fact]
    public async Task Healthz_stays_live_when_dependencies_are_down()
    {
        var res = await Client(new Fake("database", false)).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Database_check_fails_when_postgres_unreachable()
    {
        var res = await factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Proofly",
            "Host=127.0.0.1;Port=1;Username=x;Database=x;Timeout=1")).CreateClient().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("database", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_endpoint_is_absent_by_default()
    {
        var res = await Client(new Fake("database", true)).GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Evidence_check_reports_missing_readonly_and_writable_directories()
    {
        static EvidenceDirectoryReadiness For(string? dir) => new(new ConfigurationBuilder()
            .AddInMemoryCollection(dir is null ? [] : [new("Evidence:Directory", dir)]).Build());

        Assert.False((await For(null).CheckAsync(default)).Ok);
        Assert.False((await For(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).CheckAsync(default)).Ok);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.True((await For(dir).CheckAsync(default)).Ok);
            Assert.Empty(Directory.GetFiles(dir)); // probe file removed
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class StartupSelfCheckTests
{
    private static IConfiguration Cfg(params (string, string)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();

    [Fact]
    public void Valid_production_config_passes()
    {
        var errors = StartupSelfCheck.Validate(Cfg(("ConnectionStrings:Proofly", "Host=db"), ("Auth:Mode", "Oidc"), ("Evidence:Directory", "/e")), true, _ => true);
        Assert.Empty(errors);
    }

    [Fact]
    public void Production_reports_every_problem()
    {
        var errors = StartupSelfCheck.Validate(Cfg(("Auth:Mode", "Local")), true, _ => true);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("ConnectionStrings:Proofly"));
        Assert.Contains(errors, e => e.Contains("Auth:Mode=Local"));
        Assert.Contains(errors, e => e.Contains("Evidence:Directory"));
    }

    [Fact]
    public void Missing_evidence_directory_on_disk_is_rejected()
    {
        var errors = StartupSelfCheck.Validate(Cfg(("ConnectionStrings:Proofly", "Host=db"), ("Evidence:Directory", "/nope")), false, _ => false);
        Assert.Single(errors);
    }

    [Fact]
    public void Development_allows_local_auth_and_no_evidence_directory()
    {
        Assert.Empty(StartupSelfCheck.Validate(Cfg(("ConnectionStrings:Proofly", "Host=db"), ("Auth:Mode", "Local")), false));
    }

    [Fact]
    public void Startup_throws_listing_errors()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StartupSelfCheck.ThrowIfInvalid(Cfg(), true));
        Assert.Contains("ConnectionStrings:Proofly", ex.Message);
    }

    [Fact]
    public void Metrics_access_is_allow_listed_by_address_and_port()
    {
        var o = MetricsOptions.From(Cfg(("Observability:Metrics:Enabled", "true"), ("Observability:Metrics:Port", "9100")));
        Assert.True(o.Permits(IPAddress.Loopback, 9100));
        Assert.False(o.Permits(IPAddress.Loopback, 8080));
        Assert.False(o.Permits(IPAddress.Parse("10.0.0.5"), 9100));
        Assert.False(o.Permits(null, 9100));
    }
}
