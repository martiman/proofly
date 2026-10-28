using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Data.Evidence;
using Proofly.Data.Jobs;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (2): every evidence-pack generation and download is audited with actor and request id; denied attempts leak nothing.</summary>
public sealed class EvidenceAuditTests : IClassFixture<SeededDb>, IAsyncLifetime
{
    private readonly SeededDb _db;
    private SecFactory _f = null!;
    private string _scopedToA = "";

    public EvidenceAuditTests(SeededDb db) => _db = db;

    public async Task InitializeAsync()
    {
        _f = new SecFactory(_db);
        _ = _f.Server;
        _scopedToA = await _db.UserWith(_f, "evid-scoped-a", ("VIEWER", "LE-SECA"));
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private static HttpRequestMessage Post(string run, string requestId)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/runs/{run}/evidence-pack");
        m.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        m.Headers.Add("X-Request-Id", requestId);
        return m;
    }

    private static HttpRequestMessage Download(string run, string requestId)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{run}/evidence-pack/download");
        m.Headers.Add("X-Request-Id", requestId);
        return m;
    }

    private async Task<string> GenerateAs(Role role, string run, string requestId)
    {
        var res = await _f.As(role).SendAsync(Post(run, requestId));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var handler = ActivatorUtilities.CreateInstance<EvidencePackJobHandler>(_f.Services);
        await handler.HandleAsync(new GenerateEvidencePackPayload(id), new JobContext(1, 1, 5, null, null), default);
        return id;
    }

    private async Task<long> Count(string where) => await _db.Scalar<long>($"SELECT count(*) FROM audit_events WHERE {where}");

    [Fact]
    public async Task Every_generation_is_audited_with_actor_and_request_id()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var p1 = await GenerateAs(Role.Approver, "RUN-SECA", first);
        var p2 = await GenerateAs(Role.Viewer, "RUN-SECA", second);

        Assert.Equal(1, await Count($"event_type='EVIDENCE_PACK_GENERATED' AND subject_id='{p1}' AND actor_type='USER' AND actor_id='local-approver' AND request_id='{first}' AND run_id='RUN-SECA'"));
        Assert.Equal(1, await Count($"event_type='EVIDENCE_PACK_GENERATED' AND subject_id='{p2}' AND actor_type='USER' AND actor_id='local-viewer' AND request_id='{second}' AND run_id='RUN-SECA'"));
        // The worker's completion event is a SYSTEM actor without a request; every user-attributed event carries both.
        Assert.Equal(0, await Count("event_type LIKE 'EVIDENCE_PACK%' AND actor_type='USER' AND (actor_id IS NULL OR request_id IS NULL)"));
    }

    [Fact]
    public async Task Every_download_is_audited_with_actor_and_request_id_and_repeats_are_counted()
    {
        var pack = await GenerateAs(Role.Approver, "RUN-SECA", Guid.NewGuid().ToString());
        var before = await Count("event_type='EVIDENCE_PACK_DOWNLOADED'");
        var ids = new List<(Role Role, string Rid)> { (Role.Viewer, Guid.NewGuid().ToString()), (Role.Approver, Guid.NewGuid().ToString()), (Role.Viewer, Guid.NewGuid().ToString()) };
        foreach (var (role, rid) in ids)
        {
            var res = await _f.As(role).SendAsync(Download("RUN-SECA", rid));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
            Assert.True((await res.Content.ReadAsByteArrayAsync()).Length > 0);
            var actor = role == Role.Viewer ? "local-viewer" : "local-approver";
            Assert.Equal(1, await Count($"event_type='EVIDENCE_PACK_DOWNLOADED' AND subject_id='{pack}' AND actor_type='USER' AND actor_id='{actor}' AND request_id='{rid}' AND after->>'outcome'='DELIVERED'"));
        }
        Assert.Equal(before + ids.Count, await Count("event_type='EVIDENCE_PACK_DOWNLOADED'"));
    }

    [Fact]
    public async Task Denied_attempts_are_403_or_404_leak_nothing_and_never_record_a_delivery()
    {
        await GenerateAs(Role.Approver, "RUN-SECB", Guid.NewGuid().ToString());
        await GenerateAs(Role.Approver, "RUN-SECA", Guid.NewGuid().ToString());
        var sha = (await _db.Scalar<string>("SELECT sha256 FROM evidence_packs WHERE run_id='RUN-SECA' AND status='READY' LIMIT 1"))!.Trim();
        var shaB = (await _db.Scalar<string>("SELECT sha256 FROM evidence_packs WHERE run_id='RUN-SECB' AND status='READY' LIMIT 1"))!.Trim();
        var downloads = await Count("event_type='EVIDENCE_PACK_DOWNLOADED'");
        var generated = await Count("event_type='EVIDENCE_PACK_GENERATED'");
        var jobs = await _db.Scalar<long>("SELECT count(*) FROM jobs");

        var attempts = new List<(string Who, HttpClient Client, string Run, HttpStatusCode Expected)>
        {
            ("analyst", _f.As(Role.Analyst), "RUN-SECA", HttpStatusCode.Forbidden),            // no evidence.export
            ("config admin", _f.As(Role.ConfigAdmin), "RUN-SECA", HttpStatusCode.Forbidden),
            ("security admin", _f.As(Role.SecurityAdmin), "RUN-SECA", HttpStatusCode.Forbidden),
            ("scoped to A, run of B", _f.As(_scopedToA), "RUN-SECB", HttpStatusCode.NotFound), // out of scope: existence not disclosed
            ("anonymous", _f.As((string?)null), "RUN-SECA", HttpStatusCode.Unauthorized),
            ("garbage token", _f.As("not.a.token"), "RUN-SECA", HttpStatusCode.Unauthorized),
        };
        foreach (var (who, client, run, expected) in attempts)
        {
            foreach (var req in new[] { Download(run, Guid.NewGuid().ToString()), Post(run, Guid.NewGuid().ToString()) })
            {
                var res = await client.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                Assert.True(res.StatusCode == expected, $"{who}: {req.Method} {req.RequestUri} -> {(int)res.StatusCode}");
                Assert.NotEqual("application/zip", res.Content.Headers.ContentType?.MediaType);
                Assert.False(res.Content.Headers.ContentDisposition is not null || res.Headers.Contains("X-Content-SHA256"), $"{who}: download headers on a denied request");
                foreach (var leak in new[] { sha, shaB, "evidence-pack-RUN-SEC", "PK\u0003\u0004" }) { Assert.DoesNotContain(leak, body); }
                Assert.DoesNotContain("EVIDENCE_PACK", body);
            }
        }
        Assert.Equal(downloads, await Count("event_type='EVIDENCE_PACK_DOWNLOADED'"));
        Assert.Equal(generated, await Count("event_type='EVIDENCE_PACK_GENERATED'"));
        Assert.Equal(jobs, await _db.Scalar<long>("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Denied_403_attempts_are_audited_once_per_request_with_actor_and_request_id_and_404_401_are_not()
    {
        var denied = await Count("event_type='EVIDENCE_ACCESS_DENIED'");
        var ids = new List<string>();
        foreach (var client in new[] { _f.As(Role.Analyst), _f.As(Role.ConfigAdmin), _f.As(Role.SecurityAdmin) })
        {
            foreach (var make in new Func<string, HttpRequestMessage>[] { id => Download("RUN-SECA", id), id => Post("RUN-SECA", id) })
            {
                var id = Guid.NewGuid().ToString();
                ids.Add(id);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(make(id))).StatusCode);
            }
        }
        Assert.Equal(denied + ids.Count, await Count("event_type='EVIDENCE_ACCESS_DENIED'"));
        foreach (var id in ids) { Assert.Equal(1, await Count($"event_type='EVIDENCE_ACCESS_DENIED' AND request_id='{id}' AND run_id='RUN-SECA' AND actor_type='USER'")); }

        // 401 and 404 (out of scope: existence hidden) are not audited, nor is any other operation.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.As((string?)null).SendAsync(Download("RUN-SECA", Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.As(_scopedToA).SendAsync(Download("RUN-SECB", Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(denied + ids.Count, await Count("event_type='EVIDENCE_ACCESS_DENIED'"));
        Assert.Contains("EVIDENCE_ACCESS_DENIED", await _f.As(Role.Approver).GetStringAsync("/api/v1/audit-events?limit=200&type=EVIDENCE_ACCESS_DENIED"));
    }

    [Fact]
    public async Task Download_audit_events_are_visible_through_the_audit_api_with_actor_and_request_id()
    {
        await GenerateAs(Role.Approver, "RUN-SECA", Guid.NewGuid().ToString());
        var rid = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await _f.As(Role.Viewer).SendAsync(Download("RUN-SECA", rid))).StatusCode);
        var body = await _f.As(Role.Approver).GetStringAsync("/api/v1/audit-events?limit=200");
        Assert.Contains(rid, body);
        Assert.Contains("EVIDENCE_PACK_DOWNLOADED", body);
        Assert.Contains("local-viewer", body);
    }

    [Fact]
    public async Task Audit_events_cannot_be_rewritten_or_deleted()
    {
        await GenerateAs(Role.Approver, "RUN-SECA", Guid.NewGuid().ToString());
        await Assert.ThrowsAsync<PostgresException>(() => _db.Exec("UPDATE audit_events SET actor_id='someone-else' WHERE event_type='EVIDENCE_PACK_GENERATED'"));
        await Assert.ThrowsAsync<PostgresException>(() => _db.Exec("DELETE FROM audit_events WHERE event_type='EVIDENCE_PACK_GENERATED'"));
    }
}
