using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Data.Exceptions.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Exceptions.Commands;

public class ResolutionCommandsApiTests(CommandsFixture f) : IClassFixture<CommandsFixture>
{
    private HttpClient Analyst => f.As(f.TokenFor(Role.Analyst));
    private HttpClient Approver => f.As(f.TokenFor(Role.Approver));
    private HttpClient Viewer => f.As(f.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> EtagOf(HttpClient c, string id) => (await c.GetAsync("/api/v1/exceptions/" + id)).Headers.ETag!.ToString();

    private static HttpRequestMessage Post(string url, object body, string? etag, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (etag is not null) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
        req.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return req;
    }

    private Task<long> Audits(string type, string subject) =>
        f.Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type = @t AND subject_id = @s", ("t", type), ("s", subject));

    private async Task<string> Seed(string id, string status = "OPEN", bool blocking = false, string run = "R1", string le = "LE-1")
    {
        await f.Exec($"""
            INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking)
            VALUES ('{id}','{run}','{le}','2026-03-02','Res {id}','matching','NO_BANK_EVIDENCE','HIGH','{status}',7.5,'EUR','2026-03-05T09:00Z',{blocking.ToString().ToLowerInvariant()})
            ON CONFLICT DO NOTHING
            """);
        return id;
    }

    private static object Proposal(string kind = "RESOLVED") => new { kind, reason = "Bank file arrived late", evidenceRefs = new[] { "doc-1" } };

    private async Task<(string ProposalId, int Version)> ProposeAs(HttpClient c, string id, string kind = "RESOLVED")
    {
        var res = await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(kind), await EtagOf(c, id)));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var pid = await f.Scalar<string>("SELECT id FROM resolution_proposals WHERE exception_id = @e AND status = 'PENDING'", ("e", id));
        var v = await f.Scalar<int>("SELECT proposal_version FROM approvals WHERE object_id = @p", ("p", pid!));
        return (pid!, v);
    }

    private async Task<HttpResponseMessage> Decide(HttpClient c, string id, string pid, string decision, int? version, string? etag = null) =>
        await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals/{pid}/decision",
            new { decision, reason = "Reviewed the evidence", proposalVersion = version }, etag ?? await EtagOf(c, id)));

    private async Task<HttpClient> SecondApprover() => f.As(await f.ScopedToken("ap2", Role.Approver, "LE-1"));

    private Task<string?> Fingerprint(string id) => f.Scalar<string>(
        "SELECT md5(concat_ws('|', title, control, reason_code, severity, value_amount, value_currency, blocking, run_id, business_date, due_at)) FROM exceptions WHERE id = @i", ("i", id));

    [Fact]
    public async Task Propose_creates_proposal_approval_pending_status_audit_and_never_alters_the_finding()
    {
        var id = await Seed("P1");
        var before = await Fingerprint(id);
        var c = Analyst;
        var etag = await EtagOf(c, id);
        var res = await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal("ACCEPTED_DIFFERENCE"), etag));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEqual(etag, res.Headers.ETag!.ToString());
        var body = await Json(res);
        Assert.Equal("PENDING_APPROVAL", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("type").GetString() == "RESOLUTION_PROPOSED");
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM resolution_proposals WHERE exception_id = 'P1' AND version = 1 AND kind = 'ACCEPTED_DIFFERENCE' AND status = 'PENDING'"));
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM approvals WHERE object_type = 'EXCEPTION_RESOLUTION' AND run_id = 'R1' AND required_role = 'APPROVER' AND status = 'PENDING' AND proposer_user_id = 'local-analyst' AND proposal_version = 1 AND jsonb_array_length(evidence_refs) = 1 AND object_id IN (SELECT id FROM resolution_proposals WHERE exception_id = 'P1')"));
        Assert.Equal(1, await Audits("RESOLUTION_PROPOSED", id));
        Assert.Equal("Bank file arrived late", await f.Scalar<string>("SELECT reason FROM audit_events WHERE subject_id = 'P1'"));
        Assert.Equal(before, await Fingerprint(id));
        Assert.DoesNotContain("APPROVE_RESOLUTION", (await Json(res)).GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("""{"kind":"NOPE","reason":"long enough","evidenceRefs":["a"]}""")]
    [InlineData("""{"kind":"RESOLVED","reason":"ab","evidenceRefs":["a"]}""")]
    [InlineData("""{"kind":"RESOLVED","reason":"long enough","evidenceRefs":[]}""")]
    [InlineData("""{"kind":"RESOLVED","reason":"long enough","evidenceRefs":["  "]}""")]
    public async Task Propose_validation_is_422_and_changes_nothing(string json)
    {
        var id = await Seed("P2");
        var c = Analyst;
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/exceptions/{id}/resolution-proposals") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("If-Match", await EtagOf(c, id));
        req.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(req)).StatusCode);
        Assert.Equal(0, await Audits("RESOLUTION_PROPOSED", id));
        Assert.Equal("OPEN", await f.Scalar<string>("SELECT status FROM exceptions WHERE id = 'P2'"));
    }

    [Fact]
    public async Task Propose_etag_permission_scope_and_state_rules()
    {
        var id = await Seed("P3");
        var c = Analyst;
        Assert.Equal((HttpStatusCode)428, (await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(), null))).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(), "\"v99\""))).StatusCode);
        var etag = await EtagOf(c, id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(), etag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.TokenOfSubject("sub-le2")).SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(), etag))).StatusCode);
        var done = await Seed("P3-DONE", "RESOLVED");
        var r1 = await c.SendAsync(Post($"/api/v1/exceptions/{done}/resolution-proposals", Proposal(), await EtagOf(c, done)));
        Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);
        var sup = await Seed("P3-SUP", "OPEN", run: "R3");
        var r2 = await c.SendAsync(Post($"/api/v1/exceptions/{sup}/resolution-proposals", Proposal(), await EtagOf(c, sup)));
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.EndsWith("/run-superseded", (await Json(r2)).GetProperty("type").GetString());
        Assert.Equal(0, await Audits("RESOLUTION_PROPOSED", id));
    }

    [Fact]
    public async Task Propose_idempotent_replay_executes_once()
    {
        var id = await Seed("P4");
        var c = Analyst;
        var etag = await EtagOf(c, id);
        var key = Guid.NewGuid().ToString();
        var first = await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(), etag, key));
        var replay = await c.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals", Proposal(), etag, key));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await Audits("RESOLUTION_PROPOSED", id));
    }

    [Theory]
    [InlineData("RESOLVED")]
    [InlineData("ACCEPTED_DIFFERENCE")]
    [InlineData("FALSE_POSITIVE")]
    public async Task Approve_by_a_different_approver_sets_the_terminal_status_and_keeps_the_finding(string kind)
    {
        var id = await Seed("D-" + kind, blocking: true);
        var before = await Fingerprint(id);
        var (pid, v) = await ProposeAs(Analyst, id, kind);
        var ap = Approver;
        var res = await Decide(ap, id, pid, "APPROVE", v);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await Json(res);
        Assert.Equal(kind, body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("type").GetString() == "APPROVAL");
        Assert.Equal(["REOPEN"], body.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(1, await Audits("RESOLUTION_DECIDED", id));
        Assert.Equal("APPROVED", await f.Scalar<string>("SELECT status FROM resolution_proposals WHERE id = @p", ("p", pid)));
        Assert.Equal("local-approver", await f.Scalar<string>("SELECT approver_user_id FROM approvals WHERE object_id = @p", ("p", pid)));
        Assert.Equal(kind, await f.Scalar<string>("SELECT resolution_kind FROM exceptions WHERE id = @i", ("i", id)));
        Assert.Equal(before, await Fingerprint(id));
    }

    [Fact]
    public async Task Proposer_cannot_approve_their_own_proposal_even_with_the_approve_permission()
    {
        var id = await Seed("S1", blocking: true);
        var ap = Approver;
        var (pid, v) = await ProposeAs(ap, id); // an APPROVER proposes
        var res = await Decide(ap, id, pid, "APPROVE", v);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.EndsWith("/separation-of-duties", (await Json(res)).GetProperty("type").GetString());
        Assert.Equal(0, await Audits("RESOLUTION_DECIDED", id));
        Assert.Equal("PENDING_APPROVAL", await f.Scalar<string>("SELECT status FROM exceptions WHERE id = 'S1'"));
        var nonBlocking = await Seed("S2");
        var (pid2, v2) = await ProposeAs(ap, nonBlocking);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(ap, nonBlocking, pid2, "REJECT", v2)).StatusCode);
        // the pending read model agrees: no approve/reject for the proposer, but a different approver may
        var detail = await Json(await ap.GetAsync("/api/v1/exceptions/S1"));
        Assert.DoesNotContain("APPROVE_RESOLUTION", detail.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        var other = await SecondApprover();
        var d2 = await Json(await other.GetAsync("/api/v1/exceptions/S1"));
        Assert.Contains("APPROVE_RESOLUTION", d2.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Database_check_rejects_a_self_approval_even_if_the_code_did_not()
    {
        var id = await Seed("S3");
        var (pid, _) = await ProposeAs(Analyst, id);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Exec($"UPDATE approvals SET approver_user_id = proposer_user_id WHERE object_id = '{pid}'"));
    }

    [Fact]
    public async Task Decide_without_approve_permission_is_403()
    {
        var id = await Seed("S4");
        var (pid, v) = await ProposeAs(Approver, id);
        var res = await Decide(Analyst, id, pid, "APPROVE", v);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.EndsWith("/forbidden", (await Json(res)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Reject_restores_the_previous_working_status()
    {
        var id = await Seed("R-1", "IN_INVESTIGATION");
        var (pid, v) = await ProposeAs(Analyst, id);
        var res = await Decide(Approver, id, pid, "REJECT", v);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("IN_INVESTIGATION", (await Json(res)).GetProperty("status").GetString());
        Assert.Equal("REJECTED", await f.Scalar<string>("SELECT status FROM resolution_proposals WHERE id = @p", ("p", pid)));
        Assert.Null(await f.Scalar<string>("SELECT resolution_kind FROM exceptions WHERE id = 'R-1'"));
        Assert.Equal(1, await Audits("RESOLUTION_DECIDED", id));
    }

    [Fact]
    public async Task Reproposing_invalidates_the_pending_proposal_and_a_stale_decision_is_409()
    {
        var id = await Seed("V1", "WAITING_FOR_DATA");
        var (p1, v1) = await ProposeAs(Analyst, id);
        var (p2, v2) = await ProposeAs(Analyst, id, "FALSE_POSITIVE");
        Assert.NotEqual(p1, p2);
        Assert.Equal(1, v1);
        Assert.Equal(2, v2);
        Assert.Equal("INVALIDATED", await f.Scalar<string>("SELECT status FROM resolution_proposals WHERE id = @p", ("p", p1)));
        Assert.Equal("INVALIDATED", await f.Scalar<string>("SELECT status FROM approvals WHERE object_id = @p", ("p", p1)));
        var ap = Approver;
        var stale = await Decide(ap, id, p1, "APPROVE", v1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var wrongVersion = await Decide(ap, id, p2, "APPROVE", v1);
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
        Assert.Equal(0, await Audits("RESOLUTION_DECIDED", id));
        var ok = await Decide(ap, id, p2, "REJECT", v2);
        Assert.Equal("WAITING_FOR_DATA", (await Json(ok)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Decide_etag_missing_stale_state_and_unknown_proposal_rules()
    {
        var id = await Seed("V2");
        var ap = Approver;
        var notPending = await ap.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals/RP-x/decision", new { decision = "APPROVE", reason = "fine by me" }, await EtagOf(ap, id)));
        Assert.Equal(HttpStatusCode.Conflict, notPending.StatusCode);
        var (pid, v) = await ProposeAs(Analyst, id);
        var url = $"/api/v1/exceptions/{id}/resolution-proposals/{pid}/decision";
        Assert.Equal((HttpStatusCode)428, (await ap.SendAsync(Post(url, new { decision = "APPROVE", reason = "fine by me" }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await ap.SendAsync(Post(url, new { decision = "APPROVE", reason = "fine by me" }, "\"v99\""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ap.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals/RP-none/decision", new { decision = "APPROVE", reason = "fine by me" }, await EtagOf(ap, id)))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await ap.SendAsync(Post(url, new { decision = "APPROVE", reason = "x" }, await EtagOf(ap, id)))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await ap.SendAsync(Post(url, new { decision = "MAYBE", reason = "fine by me" }, await EtagOf(ap, id)))).StatusCode);
        var le2Approver = f.As(await f.ScopedToken("ap-le2", Role.Approver, "LE-2"));
        Assert.Equal(HttpStatusCode.NotFound, (await le2Approver.SendAsync(Post(url, new { decision = "APPROVE", reason = "fine by me" }, "\"v1\""))).StatusCode);
        Assert.Equal(0, await Audits("RESOLUTION_DECIDED", id));
        Assert.Equal(1, v);
    }

    [Fact]
    public async Task Decide_replay_is_idempotent_and_concurrent_decisions_have_one_winner()
    {
        var id = await Seed("V3");
        var (pid, v) = await ProposeAs(Analyst, id);
        var ap = Approver;
        var etag = await EtagOf(ap, id);
        var tasks = Enumerable.Range(0, 2).Select(_ => ap.SendAsync(Post($"/api/v1/exceptions/{id}/resolution-proposals/{pid}/decision",
            new { decision = "APPROVE", reason = "fine by me", proposalVersion = v }, etag))).ToArray();
        var codes = (await Task.WhenAll(tasks)).Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal(200, codes[0]);
        Assert.NotEqual(200, codes[1]);
        Assert.Equal(1, await Audits("RESOLUTION_DECIDED", id));

        var id2 = await Seed("V4");
        var (p2, v2) = await ProposeAs(Analyst, id2);
        var e2 = await EtagOf(ap, id2);
        var key = Guid.NewGuid().ToString();
        var body = new { decision = "APPROVE", reason = "fine by me", proposalVersion = v2 };
        var first = await ap.SendAsync(Post($"/api/v1/exceptions/{id2}/resolution-proposals/{p2}/decision", body, e2, key));
        var replay = await ap.SendAsync(Post($"/api/v1/exceptions/{id2}/resolution-proposals/{p2}/decision", body, e2, key));
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await Audits("RESOLUTION_DECIDED", id2));
    }

    [Fact]
    public async Task Reopen_returns_a_terminal_exception_to_open_and_keeps_the_finding_and_history()
    {
        var id = await Seed("O1", blocking: true);
        var before = await Fingerprint(id);
        var (pid, v) = await ProposeAs(Analyst, id);
        await Decide(Approver, id, pid, "APPROVE", v);
        var c = Analyst;
        var etag = await EtagOf(c, id);
        var res = await c.SendAsync(Post($"/api/v1/exceptions/{id}/reopen", new { reason = "New evidence found" }, etag));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Json(res);
        Assert.Equal("OPEN", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("type").GetString() == "REOPENED");
        Assert.Equal(1, await Audits("EXCEPTION_REOPENED", id));
        Assert.Equal("New evidence found", await f.Scalar<string>("SELECT reason FROM audit_events WHERE event_type = 'EXCEPTION_REOPENED' AND subject_id = 'O1'"));
        Assert.Equal(before, await Fingerprint(id));
        Assert.Equal("APPROVED", await f.Scalar<string>("SELECT status FROM resolution_proposals WHERE id = @p", ("p", pid)));
        Assert.Contains("PROPOSE_RESOLUTION", body.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Reopen_validation_state_permission_and_superseded_rules()
    {
        var id = await Seed("O2", "FALSE_POSITIVE");
        var c = Analyst;
        var etag = await EtagOf(c, id);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post($"/api/v1/exceptions/{id}/reopen", new { reason = "no" }, etag))).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await c.SendAsync(Post($"/api/v1/exceptions/{id}/reopen", new { reason = "Because" }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await c.SendAsync(Post($"/api/v1/exceptions/{id}/reopen", new { reason = "Because" }, "\"v99\""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.SendAsync(Post($"/api/v1/exceptions/{id}/reopen", new { reason = "Because" }, etag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.TokenOfSubject("sub-le2")).SendAsync(Post($"/api/v1/exceptions/{id}/reopen", new { reason = "Because" }, etag))).StatusCode);
        var open = await Seed("O3");
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Post($"/api/v1/exceptions/{open}/reopen", new { reason = "Because" }, await EtagOf(c, open)))).StatusCode);
        var sup = await Seed("O4", "RESOLVED", run: "R3");
        var rs = await c.SendAsync(Post($"/api/v1/exceptions/{sup}/reopen", new { reason = "Because" }, await EtagOf(c, sup)));
        Assert.Equal(HttpStatusCode.Conflict, rs.StatusCode);
        Assert.EndsWith("/run-superseded", (await Json(rs)).GetProperty("type").GetString());
        Assert.Equal(0, await Audits("EXCEPTION_REOPENED", id) + await Audits("EXCEPTION_REOPENED", open) + await Audits("EXCEPTION_REOPENED", sup));
    }

    [Fact]
    public async Task Unresolved_blocking_query_counts_resolved_accepted_and_false_positive_as_cleared()
    {
        await f.Exec("""
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('RB','LE-1','2026-03-09',1,'rs-1','REVIEW_REQUIRED') ON CONFLICT DO NOTHING
            """);
        var repo = f.Factory.Services.GetRequiredService<IResolutionCommandRepository>();
        Assert.False(await repo.HasUnresolvedBlockingExceptionsAsync("RB"));
        await Seed("B-OPEN", "OPEN", true, "RB");
        Assert.True(await repo.HasUnresolvedBlockingExceptionsAsync("RB"));
        await f.Exec("UPDATE exceptions SET status = 'PENDING_APPROVAL' WHERE id = 'B-OPEN'");
        Assert.True(await repo.HasUnresolvedBlockingExceptionsAsync("RB"));
        foreach (var s in new[] { "RESOLVED", "ACCEPTED_DIFFERENCE", "FALSE_POSITIVE" })
        {
            await f.Exec($"UPDATE exceptions SET status = '{s}' WHERE id = 'B-OPEN'");
            Assert.False(await repo.HasUnresolvedBlockingExceptionsAsync("RB"));
        }
        await Seed("B-NONBLOCK", "OPEN", false, "RB"); // non-blocking never blocks
        Assert.False(await repo.HasUnresolvedBlockingExceptionsAsync("RB"));
    }
}
