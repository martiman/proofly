using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proofly.Api.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Security.Tests.Support;

/// <summary>Controllable clock: the local token service and the hosting layer read time from here.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The API host under test with explicit settings, captured logs and an optional fake clock.</summary>
public sealed class SecFactory(SecDb db, string mode = "Local", string environment = "Development",
    IDictionary<string, string?>? settings = null, TestClock? clock = null, string? connectionString = null) : WebApplicationFactory<Program>
{
    public const string SigningKey = "unit-test-signing-key-ONLY-0123456789abcdef-0123456789abcdef-0123456789";
    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Proofly", connectionString ?? db.ConnectionString);
        builder.UseSetting("Auth:Mode", mode);
        builder.UseSetting("Evidence:Directory", db.EvidenceDir);
        if (mode == "Local") { builder.UseSetting("Auth:Local:SigningKey", SigningKey); }
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) { builder.UseSetting(k, v); }
        builder.ConfigureTestServices(s =>
        {
            // Last registration wins: every ILogger<T> in the host now writes into the capture (all levels).
            s.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs)));
            if (clock is not null) { s.AddSingleton<TimeProvider>(clock); }
        });
    }

    public HttpClient As(string? token)
    {
        var c = CreateClient();
        if (token is not null) { c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        return c;
    }

    public LocalTokenService Tokens => Services.GetRequiredService<LocalTokenService>();
    public string TokenFor(Role role) => Tokens.Issue(LocalUsers.Subject(role));
    /// <summary>Contract operations that have no route in the API yet (METHOD + template), so matrix tests do not mistake a missing route for a security decision.</summary>
    public IReadOnlySet<string> UnimplementedOperations()
    {
        var routes = Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(d => d.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => Key(m, e.RoutePattern.RawText)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Contract.Operations.Select(o => (Op: o, Key: Key(o.Method, "/api/v1" + o.Template))).Where(x => !routes.Contains(x.Key)).Select(x => x.Op.Method + " /api/v1" + x.Op.Template).ToHashSet();
    }

    private static string Key(string method, string? pattern) =>
        method.ToUpperInvariant() + " " + System.Text.RegularExpressions.Regex.Replace((pattern ?? "").TrimEnd('/'), @"\{[^}]+\}", "{}");

    public HttpClient As(Role role) => As(TokenFor(role));
}
