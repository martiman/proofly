using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Data.Evidence;
using Proofly.Data.Jobs;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Evidence;

internal sealed class EvFactory(string schema, string dir) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Proofly", $"{EvFixture.Cs};Search Path={schema}");
        builder.UseSetting("Auth:Mode", "Local");
        builder.UseSetting("Evidence:Directory", dir);
    }
}

public sealed class EvFixture : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "proofly-evapi-" + Guid.NewGuid().ToString("N"));
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal EvFactory Factory { get; private set; } = null!;
    internal string ScopedToken = "";

    public async Task<T?> Scalar<T>(string sql) { await using var c = Db.CreateCommand(sql); var v = await c.ExecuteScalarAsync(); return v is null or DBNull ? default : (T)v; }
    public async Task Exec(string sql) { await using var c = Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs)) { await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\""); await c.ExecuteNonQueryAsync(); }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={Schema}");
        await new MigrationRunner(Db).RunAsync();
        Directory.CreateDirectory(Dir);
        Factory = new EvFactory(Schema, Dir);
        _ = Factory.Server;
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-scoped", "Scoped Exporter", "scoped@x.test", "sub-scoped", true, [new RoleAssignment("VIEWER", "LE1")]));
        ScopedToken = Factory.Services.GetRequiredService<LocalTokenService>().Issue("sub-scoped");
        await Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE1','Entity One','GBP'),('LE2','Entity Two','EUR');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES
              ('R1','LE1','2026-10-27',1,'v1','REVIEW_REQUIRED'),('R2','LE2','2026-10-27',1,'v1','REVIEW_REQUIRED'),('R3','LE1','2026-10-28',1,'v1','REVIEW_REQUIRED');
            """);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync(); await Db.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        await using var admin = NpgsqlDataSource.Create(Cs); await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE"); await c.ExecuteNonQueryAsync();
    }
}

public class EvidenceApiTests(EvFixture f) : IClassFixture<EvFixture>
{
    private HttpClient As(string? token) { var c = f.Factory.CreateClient(); if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private HttpClient As(Role r) => As(f.Factory.Services.GetRequiredService<LocalTokenService>().Issue(LocalUsers.Subject(r)));

    private static HttpRequestMessage Post(string run, string? key = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/runs/{run}/evidence-pack");
        m.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return m;
    }

    private async Task Generate(string packId)
    {
        var handler = ActivatorUtilities.CreateInstance<EvidencePackJobHandler>(f.Factory.Services);
        await handler.HandleAsync(new GenerateEvidencePackPayload(packId), new JobContext(1, 1, 5, null, null), default);
    }

    [Fact]
    public async Task Generate_returns_202_generating_enqueues_job_with_run_id_and_audits()
    {
        var res = await As(Role.Approver).SendAsync(Post("R1"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("GENERATING", j.GetProperty("status").GetString());
        var id = j.GetProperty("id").GetString()!;
        Assert.Equal(1, await f.Scalar<long>("SELECT count(*) FROM jobs WHERE type='evidence_pack.generate' AND run_id='R1'"));
        Assert.Equal(1, await f.Scalar<long>($"SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_GENERATED' AND subject_id='{id}' AND actor_id='local-approver'"));

        var get = await As(Role.Viewer).GetAsync("/api/v1/runs/R1/evidence-pack");
        var generating = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("GENERATING", generating.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, generating.GetProperty("manifest").ValueKind);
        Assert.Equal(JsonValueKind.Null, generating.GetProperty("auditChain").ValueKind);

        // A second request while generating is a 409; an idempotent replay returns the original response.
        Assert.Equal(HttpStatusCode.Conflict, (await As(Role.Approver).SendAsync(Post("R1"))).StatusCode);
        var key = Guid.NewGuid().ToString();
        var first = await As(Role.Viewer).SendAsync(Post("R3", key));
        var again = await As(Role.Viewer).SendAsync(Post("R3", key));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await f.Scalar<long>("SELECT count(*) FROM evidence_packs WHERE run_id='R3'"));
    }

    [Fact]
    public async Task Generate_requires_permission_scope_and_existing_run()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Role.Analyst).SendAsync(Post("R2"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(f.ScopedToken).SendAsync(Post("R2"))).StatusCode); // out of scope: not disclosed
        Assert.Equal(HttpStatusCode.NotFound, (await As(Role.Viewer).SendAsync(Post("nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As((string?)null).SendAsync(Post("R2"))).StatusCode);
        Assert.Equal(0, await f.Scalar<long>("SELECT count(*) FROM evidence_packs WHERE run_id='R2'"));
    }

    [Fact]
    public async Task Download_streams_verified_pack_and_every_download_is_audited_then_tamper_is_a_500()
    {
        // Not generated yet: 404, and permission is required.
        Assert.Equal(HttpStatusCode.NotFound, (await As(Role.Viewer).GetAsync("/api/v1/runs/R2/evidence-pack/download")).StatusCode);

        await f.Exec("INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('R9','LE1','2026-11-01',1,'v1','REVIEW_REQUIRED')");
        var post = await As(Role.Approver).SendAsync(Post("R9"));
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await Generate(id);

        Assert.Equal(HttpStatusCode.Forbidden, (await As(Role.Analyst).GetAsync("/api/v1/runs/R9/evidence-pack/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(f.ScopedToken).GetAsync("/api/v1/runs/R2/evidence-pack/download")).StatusCode);
        Assert.Equal(0, await f.Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_DOWNLOADED'"));

        var rid = Guid.NewGuid().ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/R9/evidence-pack/download");
        req.Headers.Add("X-Request-Id", rid);
        var ok = await As(Role.Viewer).SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/zip", ok.Content.Headers.ContentType!.MediaType);
        var bytes = await ok.Content.ReadAsByteArrayAsync();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(sha, (await f.Scalar<string>($"SELECT sha256 FROM evidence_packs WHERE id='{id}'"))!.Trim());
        var status = JsonDocument.Parse(await As(Role.Viewer).GetStringAsync("/api/v1/runs/R9/evidence-pack")).RootElement;
        Assert.Equal("GENERATED", status.GetProperty("status").GetString());
        Assert.Equal(bytes.Length, status.GetProperty("sizeBytes").GetInt64());
        // Structured manifest and audit-chain verification (contract batch 3) equal what is inside the zip.
        var manifest = status.GetProperty("manifest");
        Assert.Equal("1", manifest.GetProperty("schemaVersion").GetString());
        Assert.Equal(64, manifest.GetProperty("resultHash").GetString()!.Length);
        Assert.Equal("local-approver", manifest.GetProperty("generatedBy").GetProperty("id").GetString());
        var listed = manifest.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(listed.Order(StringComparer.Ordinal).ToArray(), listed);
        Assert.Contains("audit/chain_verification.json", listed);
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            var inZip = JsonDocument.Parse(new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd()).RootElement;
            Assert.Equal(inZip.GetProperty("resultHash").GetString(), manifest.GetProperty("resultHash").GetString());
            Assert.Equal(inZip.GetProperty("files").GetArrayLength(), listed.Length);
            Assert.Equal(listed.Length + 1, zip.Entries.Count);
        }
        var chain = status.GetProperty("auditChain");
        Assert.True(chain.GetProperty("valid").GetBoolean());
        Assert.True(chain.GetProperty("eventsChecked").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.Null, chain.GetProperty("firstBreak").ValueKind);
        Assert.Equal(1, await f.Scalar<long>($"SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_DOWNLOADED' AND subject_id='{id}' AND actor_id='local-viewer' AND request_id='{rid}'"));

        await As(Role.Viewer).GetAsync("/api/v1/runs/R9/evidence-pack/download");
        Assert.Equal(2, await f.Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_DOWNLOADED'"));

        // Tamper with the stored bytes: refused with a 500 problem and audited as an integrity failure.
        var path = Path.Combine(f.Dir, sha[..2], sha);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, [1, 2, 3]);
        var bad = await As(Role.Viewer).GetAsync("/api/v1/runs/R9/evidence-pack/download");
        Assert.Equal(HttpStatusCode.InternalServerError, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, await f.Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_DOWNLOADED' AND after->>'outcome'='INTEGRITY_FAILURE'"));
    }
}
