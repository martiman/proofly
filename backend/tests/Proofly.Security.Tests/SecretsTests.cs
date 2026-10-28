using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Api.Auth;
using Proofly.Data.Evidence;
using Proofly.Data.Jobs;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>
/// Issue #52 (1): seeded fake secrets (connection-string password, bearer tokens, signing key) must never appear in logs, problem
/// responses, audit events, exports or configuration/read endpoints.
/// </summary>
public sealed class SecretsTests : IClassFixture<SeededDb>
{
    private const string Password = "Pw-Zx81-NOT-A-REAL-PASSWORD-q7";
    private const string SigningKey = "k3y-NOT-A-REAL-SIGNING-KEY-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEFGHIJKLMNOP";
    private const string BadBearer = "Bearer-SECRET-0f9e8d7c6b5a-not-a-jwt";
    private readonly SeededDb _db;

    public SecretsTests(SeededDb db) => _db = db;

    private static Dictionary<string, string?> Settings() => new() { ["Auth:Local:SigningKey"] = SigningKey };

    private SecFactory Local() => new(_db, settings: Settings(), connectionString: _db.ConnectionString + ";Password=" + Password);

    private static string[] SecretsFor(string validToken) =>
        [Password, SigningKey, BadBearer, validToken, validToken.Split('.')[2], Convert.ToBase64String(Encoding.UTF8.GetBytes(SigningKey))];

    private static void AssertNone(string where, string haystack, IEnumerable<string> secrets)
    {
        foreach (var s in secrets) { Assert.False(haystack.Contains(s, StringComparison.Ordinal), $"A secret ({Mask(s)}) appears in {where}"); }
    }

    private static string Mask(string s) => s[..Math.Min(6, s.Length)] + "...";

    [Fact]
    public async Task Secrets_never_appear_in_logs_problem_responses_audit_events_or_exports()
    {
        await using var f = Local();
        _ = f.Server;
        var token = f.TokenFor(Role.Approver);
        var secrets = SecretsFor(token);
        var seen = new StringBuilder();

        async Task Record(HttpResponseMessage res)
        {
            seen.AppendLine(res.StatusCode + " " + res.Headers + " " + res.Content.Headers + await res.Content.ReadAsStringAsync());
        }

        var ok = f.As(token);
        var other = f.As(Role.Viewer);
        // Successes, 401 (valid + garbage + expired + wrong-key tokens), 403, 404, 422, 405.
        await Record(await ok.GetAsync("/api/v1/me"));
        await Record(await ok.GetAsync("/api/v1/runs/RUN-SECA"));
        await Record(await f.As(BadBearer).GetAsync("/api/v1/me"));
        await Record(await f.As(Jwt.Hmac("local:approver", "proofly-local", "proofly-api", Encoding.UTF8.GetBytes("wrong-key-wrong-key-wrong-key-wrong-key-1234"))).GetAsync("/api/v1/me"));
        await Record(await f.As(f.Tokens.Issue("local:approver", TimeSpan.FromMinutes(1), issuedAt: DateTimeOffset.UtcNow.AddHours(-1))).GetAsync("/api/v1/me"));
        await Record(await other.PostAsync("/api/v1/runs/RUN-SECA/sign-off/approve", null));
        await Record(await ok.GetAsync("/api/v1/runs/does-not-exist"));
        await Record(await ok.GetAsync("/api/v1/runs?limit=banana&unknown=1"));
        await Record(await ok.DeleteAsync("/api/v1/me"));
        await Record(await f.CreateClient().GetAsync("/readyz"));
        await Record(await f.CreateClient().GetAsync("/healthz"));
        var rid = Guid.NewGuid().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/runs/RUN-SECA/evidence-pack");
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        req.Headers.Add("X-Request-Id", rid);
        var generate = await ok.SendAsync(req);
        await Record(generate);
        var packId = JsonDocument.Parse(await generate.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await ActivatorUtilities.CreateInstance<EvidencePackJobHandler>(f.Services).HandleAsync(new GenerateEvidencePackPayload(packId), new JobContext(1, 1, 5, null, null), default);

        // Export: the evidence pack itself, every entry decompressed.
        var download = await ok.GetAsync("/api/v1/runs/RUN-SECA/evidence-pack/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var zipBytes = await download.Content.ReadAsByteArrayAsync();
        var exportText = new StringBuilder();
        using (var zip = new ZipArchive(new MemoryStream(zipBytes)))
        {
            Assert.NotEmpty(zip.Entries);
            foreach (var e in zip.Entries) { using var r = new StreamReader(e.Open()); exportText.AppendLine(e.FullName).AppendLine(await r.ReadToEndAsync()); }
        }
        AssertNone("the evidence pack export", exportText.ToString(), secrets);
        AssertNone("the raw evidence pack bytes", Encoding.Latin1.GetString(zipBytes), secrets);

        AssertNone("HTTP responses (bodies and headers)", seen.ToString(), secrets);
        AssertNone("captured logs (all levels)", f.Logs.All, secrets);
        Assert.True(f.Logs.Count > 0, "the log capture did not record anything");
        var audit = await _db.Scalar<string>("SELECT COALESCE(jsonb_agg(to_jsonb(a))::text, '') FROM audit_events a");
        AssertNone("audit events", audit!, secrets);
        AssertNone("the jobs table", (await _db.Scalar<string>("SELECT COALESCE(jsonb_agg(to_jsonb(j))::text, '') FROM jobs j"))!, secrets);
        AssertNone("idempotency records", (await _db.Scalar<string>("SELECT COALESCE(jsonb_agg(to_jsonb(i))::text, '') FROM idempotency_keys i"))!, secrets);
    }

    [Fact]
    public async Task A_database_failure_does_not_leak_the_connection_string_password_into_responses_or_logs()
    {
        using var idp = new FakeIdp();
        // Port 1 refuses connections: every database call fails while the token is perfectly valid.
        await using var f = new SecFactory(_db, "Oidc", settings: new Dictionary<string, string?>
        {
            ["Auth:Oidc:Authority"] = idp.Authority, ["Auth:Oidc:Audience"] = FakeIdp.Audience, ["Auth:Oidc:RequireHttpsMetadata"] = "false",
        }, connectionString: $"Host=127.0.0.1;Port=1;Database=proofly;Username=proofly;Password={Password};Timeout=2;Command Timeout=2;Pooling=false");
        _ = f.Server;
        var token = idp.Mint("oidc-user-1");
        var secrets = new[] { Password, token, token.Split('.')[2] };
        var seen = new StringBuilder();
        foreach (var path in new[] { "/api/v1/me", "/api/v1/runs", "/readyz", "/api/v1/runs/RUN-SECA/evidence-pack" })
        {
            var res = await f.As(token).GetAsync(path);
            Assert.True(res.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable, $"{path} -> {(int)res.StatusCode}: the database failure did not happen");
            seen.AppendLine($"{path} {(int)res.StatusCode} {res.Headers} {await res.Content.ReadAsStringAsync()}");
        }
        Assert.True(f.Logs.Count > 0);
        AssertNone("responses of a failing database", seen.ToString(), secrets);
        AssertNone("logs of a failing database", f.Logs.All, secrets);
        Assert.DoesNotContain("Host=127.0.0.1", seen.ToString());
        Assert.DoesNotContain("Port=1", seen.ToString());
        Assert.DoesNotContain("Username=", seen.ToString());
    }

    [Fact]
    public async Task Startup_validation_errors_do_not_echo_the_secrets_in_the_configuration()
    {
        await using var f = new SecFactory(_db, "Local", "Production", Settings(), connectionString: $"Host=localhost;Database=proofly_dev;Username=proofly;Password={Password}");
        var ex = Assert.ThrowsAny<Exception>(() => _ = f.Server);
        var text = string.Join(" | ", Chain(ex).Select(e => e.ToString()));
        AssertNone("the startup failure", text, [Password, SigningKey]);
        AssertNone("logs during a failed startup", f.Logs.All, [Password, SigningKey]);
    }

    private static IEnumerable<Exception> Chain(Exception? e) { while (e is not null) { yield return e; e = e.InnerException; } }

    [Fact]
    public async Task Configuration_and_read_endpoints_never_return_credentials()
    {
        await using var f = Local();
        _ = f.Server;
        var secrets = SecretsFor(f.TokenFor(Role.Approver));
        var forbiddenNames = new Regex("password|passwd|secret|credential|connectionstring|connection_string|signingkey|signing_key|apikey|api_key|privatekey|private_key|accesstoken|refreshtoken|bearer|clientsecret",
            RegexOptions.IgnoreCase);
        var ids = await _db.IdsOf("A");
        var checkedEndpoints = 0;
        foreach (var role in new[] { Role.Approver, Role.ConfigAdmin, Role.SecurityAdmin })
        {
            var client = f.As(role);
            foreach (var op in Contract.WithPermission.Where(o => o.Method == "GET" && !SeededDb.GlobalIds.Overlaps(Contract.PathParams(o))))
            {
                var res = await client.GetAsync(Contract.Url(op, p => ids.TryGetValue(p, out var v) ? v : "x", true, p => p is "against" or "runId" ? ids["runId"] : null));
                if (res.StatusCode != HttpStatusCode.OK) { continue; }
                var body = await res.Content.ReadAsStringAsync();
                if (!res.Content.Headers.ContentType!.MediaType!.EndsWith("json", StringComparison.Ordinal)) { continue; }
                checkedEndpoints++;
                AssertNone($"{op.Template} as {role}", body, secrets);
                foreach (var name in PropertyNames(JsonDocument.Parse(body).RootElement))
                {
                    Assert.False(forbiddenNames.IsMatch(name), $"{op.Template} exposes a property named '{name}'");
                }
            }
        }
        Assert.True(checkedEndpoints > 40, $"only {checkedEndpoints} endpoint responses were inspected");
    }

    [Fact]
    public void The_contract_declares_no_credential_properties_in_any_schema()
    {
        using var s = typeof(Proofly.Api.Platform.Contract.OpenApiContract).Assembly.GetManifestResourceStream("openapi.yaml")!;
        var yaml = new StreamReader(s).ReadToEnd();
        var props = Regex.Matches(yaml, @"^\s+([A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct();
        var offenders = props.Where(n => Regex.IsMatch(n, "password|passwd|secret|credential|connectionString|signingKey|apiKey|privateKey|accessToken|refreshToken|clientSecret", RegexOptions.IgnoreCase)).ToList();
        Assert.True(offenders.Count == 0, "Contract schema/field names that suggest credentials: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> PropertyNames(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    yield return p.Name;
                    foreach (var n in PropertyNames(p.Value)) { yield return n; }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) { foreach (var n in PropertyNames(item)) { yield return n; } }
                break;
        }
    }
}
