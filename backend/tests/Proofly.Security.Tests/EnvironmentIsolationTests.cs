using Microsoft.Extensions.Configuration;
using Proofly.Api.Observability;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (4) and (5): Local auth is refused in Production, and the startup self-check refuses development defaults in Production.</summary>
public sealed class EnvironmentIsolationTests : IClassFixture<SecDb>
{
    private readonly SecDb _db;
    public EnvironmentIsolationTests(SecDb db) => _db = db;

    private static IConfiguration Cfg(params (string, string?)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();

    private static readonly (string, string?)[] GoodProduction =
    [
        ("ConnectionStrings:Proofly", "Host=db.internal;Port=5432;Database=proofly;Username=proofly;Password=prod-secret-pw;SSL Mode=Require"),
        ("Auth:Mode", "Oidc"), ("Evidence:Directory", "/var/lib/proofly/evidence"),
    ];

    private static IReadOnlyList<string> Prod(params (string, string?)[] overrides)
    {
        var map = GoodProduction.ToDictionary(x => x.Item1, x => x.Item2);
        foreach (var (k, v) in overrides) { map[k] = v; }
        return StartupSelfCheck.Validate(Cfg(map.Select(kv => (kv.Key, kv.Value)).ToArray()), true, _ => true);
    }

    [Fact]
    public void Control_a_complete_production_configuration_passes() => Assert.Empty(Prod());

    [Theory]
    [InlineData("Local")]
    [InlineData("local")]
    [InlineData("LOCAL")]
    public void Local_auth_mode_is_refused_in_production(string mode) =>
        Assert.Contains(Prod(("Auth:Mode", mode)), e => e.Contains("Auth:Mode=Local"));

    [Fact]
    public void Local_auth_mode_is_allowed_outside_production() =>
        Assert.Empty(StartupSelfCheck.Validate(Cfg(("ConnectionStrings:Proofly", "Host=localhost;Database=proofly_dev"), ("Auth:Mode", "Local")), false, _ => true));

    [Fact]
    public void Production_refuses_a_missing_or_nonexistent_evidence_directory()
    {
        Assert.Contains(Prod(("Evidence:Directory", null)), e => e.Contains("Evidence:Directory"));
        Assert.Contains(Prod(("Evidence:Directory", "  ")), e => e.Contains("Evidence:Directory"));
        var errors = StartupSelfCheck.Validate(Cfg(GoodProduction), true, _ => false);
        Assert.Contains(errors, e => e.Contains("does not exist"));
    }

    [Theory]
    [InlineData("Host=localhost;Port=55432;Username=proofly;Database=proofly_dev")]            // the dev compose database, trust auth
    [InlineData("Host=localhost;Port=55432;Username=proofly;Database=proofly_test")]           // the test database
    [InlineData("Host=db.internal;Username=proofly;Password=x;Database=proofly_dev")]          // dev store name on a production host
    [InlineData("Host=db.internal;Username=proofly;Password=x;Database=PROOFLY_TEST")]
    [InlineData("Host=localhost;Database=proofly;Username=proofly")]                           // loopback without any credential = trust auth marker
    [InlineData("Host=127.0.0.1;Database=proofly;Username=proofly")]
    [InlineData("Host=::1;Database=proofly;Username=proofly")]
    public void Production_refuses_development_database_defaults(string connectionString)
    {
        var errors = Prod(("ConnectionStrings:Proofly", connectionString));
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.DoesNotContain(connectionString, e)); // the message never echoes the connection string
    }

    [Theory]
    [InlineData("Host=localhost;Database=proofly;Username=proofly;Password=real-pw")]          // single-box installs with a real credential are legitimate
    [InlineData("Host=db.internal;Database=proofly;Username=proofly;Passfile=/etc/proofly/pgpass")]
    public void Production_accepts_a_loopback_or_passfile_connection_with_a_real_credential(string connectionString) =>
        Assert.Empty(Prod(("ConnectionStrings:Proofly", connectionString)));

    [Fact]
    public void Production_refuses_a_connection_string_that_cannot_be_parsed_without_echoing_it()
    {
        var errors = Prod(("ConnectionStrings:Proofly", "Host=db;Password=topsecret;;;bogus-keyword=1"));
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.DoesNotContain("topsecret", e));
    }

    [Fact]
    public void Production_refuses_leftover_local_signing_key_and_token_logging()
    {
        Assert.Contains(Prod(("Auth:Local:SigningKey", "sample-signing-key-sample-signing-key-1234")), e => e.Contains("Auth:Local"));
        Assert.Contains(Prod(("Auth:Local:LogTokens", "true")), e => e.Contains("Auth:Local"));
        Assert.All(Prod(("Auth:Local:SigningKey", "sample-signing-key-sample-signing-key-1234")), e => Assert.DoesNotContain("sample-signing-key", e));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    public void Production_refuses_wildcard_and_development_cors_origins(string origin) =>
        Assert.Contains(Prod(("Cors:AllowedOrigins:0", origin)), e => e.Contains("Cors:AllowedOrigins"));

    [Fact]
    public void Production_accepts_an_explicit_https_origin() => Assert.Empty(Prod(("Cors:AllowedOrigins:0", "https://proofly.example.com")));

    [Fact]
    public async Task Production_host_does_not_start_with_local_auth()
    {
        await using var f = new SecFactory(_db, "Local", "Production", connectionString: _db.ConnectionString + ";Password=prod-pw");
        var ex = Assert.ThrowsAny<Exception>(() => _ = f.Server);
        Assert.Contains("Auth:Mode=Local", Flatten(ex));
    }

    [Fact]
    public async Task Production_host_does_not_start_with_the_development_database()
    {
        await using var f = new SecFactory(_db, "Oidc", "Production", new Dictionary<string, string?> { ["Auth:Oidc:Authority"] = "https://idp.example.com", ["Auth:Oidc:Audience"] = "x" });
        // The test connection string is localhost + proofly_test + no password: every one of those is a development marker.
        var ex = Assert.ThrowsAny<Exception>(() => _ = f.Server);
        Assert.Contains("ConnectionStrings:Proofly", Flatten(ex));
    }

    [Fact]
    public async Task Production_host_does_not_start_without_an_existing_evidence_directory()
    {
        await using var f = new SecFactory(_db, "Oidc", "Production", new Dictionary<string, string?> { ["Evidence:Directory"] = Path.Combine(_db.EvidenceDir, "missing") },
            connectionString: "Host=db.internal;Database=proofly;Username=proofly;Password=prod-pw");
        var ex = Assert.ThrowsAny<Exception>(() => _ = f.Server);
        Assert.Contains("Evidence:Directory", Flatten(ex));
    }

    private static string Flatten(Exception e) => string.Join(" | ", Chain(e).Select(x => x.Message));
    private static IEnumerable<Exception> Chain(Exception? e) { while (e is not null) { yield return e; e = e.InnerException; } }
}
