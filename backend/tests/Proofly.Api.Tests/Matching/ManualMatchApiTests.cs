using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Matching;

public sealed class ManualMatchFixture : IAsyncLifetime
{
    private readonly MatchingFixture _inner = new();
    internal Proofly.Api.Tests.Auth.AuthFactory Factory => _inner.Factory;
    internal NpgsqlDataSource Db => _inner.Db;

    public async Task InitializeAsync()
    {
        await _inner.InitializeAsync();
        // Extra unmatched EUR/GBP evidence records for the manual-match scenarios (neutral data).
        await using var c = Db.CreateCommand("""
            INSERT INTO bank_transactions (delivery_id, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency, debit_credit_indicator, bank_reference, transaction_status)
            SELECT 'D-M1', 'B-E' || n, 'X-1', '2026-03-02T16:00:00Z', '2026-03-02', 250, 'EUR', 'CREDIT', NULL, 'BOOKED' FROM generate_series(1, 10) n;
            UPDATE bank_transactions SET amount = 249.5 WHERE external_transaction_id = 'B-E8';
            INSERT INTO transfer_instructions (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status)
            SELECT 'D-M1', 'row-x' || n, 'I-X' || n, 'A-1', 'LE-M1', 'TRANSFER', 250, 'EUR', '2026-03-02T14:00:00Z', 'SETTLED' FROM generate_series(1, 10) n;
            """);
        await c.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _inner.DisposeAsync();
}

public class ManualMatchApiTests(ManualMatchFixture f) : IClassFixture<ManualMatchFixture>
{
    private HttpClient Analyst => f.Factory.As(f.Factory.TokenFor(Role.Analyst));
    private HttpClient Approver => f.Factory.As(f.Factory.TokenFor(Role.Approver));
    private HttpClient Viewer => f.Factory.As(f.Factory.TokenFor(Role.Viewer));

    private async Task<string> Id(string sql)
    {
        await using var q = f.Db.CreateCommand(sql);
        return (string)(await q.ExecuteScalarAsync())!;
    }
    private Task<string> Trf(string row) => Id($"SELECT 'TRF-' || id FROM transfer_instructions WHERE source_row_id = '{row}'");
    private Task<string> Bnk(string ext) => Id($"SELECT 'BNK-' || id FROM bank_transactions WHERE external_transaction_id = '{ext}'");
    private async Task<long> Count(string sql)
    {
        await using var q = f.Db.CreateCommand(sql);
        return (long)(await q.ExecuteScalarAsync())!;
    }

    private static HttpRequestMessage Post(string url, object body, string? ifMatch = null, string? key = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        r.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (ifMatch is not null) { r.Headers.TryAddWithoutValidation("If-Match", ifMatch); }
        return r;
    }
    private static object Proposal(string[] left, string[] right, string reason = "Same payment, bank omitted the reference") =>
        new { leftTransactionIds = left, rightTransactionIds = right, reason, evidenceRefs = new[] { "ev-77" } };
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Slug(JsonElement problem) => problem.GetProperty("type").GetString()!.Split('/').Last();

    private async Task<(string GroupId, string ETag)> ProposeOk(HttpClient c, string[] left, string[] right)
    {
        var res = await c.SendAsync(Post("/api/v1/runs/R-M1/match-groups", Proposal(left, right)));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await Json(res);
        return (body.GetProperty("id").GetString()!, res.Headers.ETag!.Tag);
    }

    private Task<HttpResponseMessage> Decide(HttpClient c, string groupId, string decision, string? etag, string reason = "Reviewed the evidence", string? key = null, object? extra = null) =>
        c.SendAsync(Post($"/api/v1/runs/R-M1/match-groups/{groupId}/decision", new { decision, reason }, etag, key));

    [Fact]
    public async Task Propose_creates_pending_group_with_approval_difference_and_audit()
    {
        var (g, etag) = await ProposeOk(Analyst, [await Trf("row-x8")], [await Bnk("B-E8")]);
        Assert.Equal("\"v1\"", etag);
        var list = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?status=PENDING_APPROVAL"));
        var grp = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == g);
        Assert.Equal("1:1", grp.GetProperty("cardinality").GetString());
        Assert.Equal("0.50", grp.GetProperty("difference").GetProperty("amount").GetString()); // 250 - 249.5, recorded not repaired
        Assert.True(grp.GetProperty("manual").GetBoolean());
        Assert.Equal("MANUAL_MATCH", grp.GetProperty("approval").GetProperty("objectType").GetString());
        Assert.Equal("PENDING", grp.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal("Same payment, bank omitted the reference", grp.GetProperty("rationale").GetString());
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_PROPOSED' AND subject_id = '{g}' AND reason IS NOT NULL"));
        // The proposer (an analyst) is told the group is not theirs to decide; the source group is untouched.
        Assert.Equal(JsonValueKind.Array, grp.GetProperty("allowedActions").ValueKind);
        Assert.Equal(0, grp.GetProperty("allowedActions").GetArrayLength());
        Assert.False(grp.GetProperty("approval").GetProperty("canDecide").GetBoolean());
        Assert.Equal(1, await Count("SELECT count(*) FROM match_candidates WHERE match_group_id = 'G-002'"));
    }

    [Fact]
    public async Task Propose_validates_input_and_rejects_without_audit()
    {
        var before = await Count("SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_PROPOSED'");
        var gbp = await Trf("row-1"); var jpy = await Trf("row-3"); var b9 = await Bnk("B-9");
        async Task<JsonElement> Bad(object body)
        {
            var r = await Analyst.SendAsync(Post("/api/v1/runs/R-M1/match-groups", body));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            return await Json(r);
        }
        await Bad(Proposal([await Trf("row-5")], [b9], "ab")); // reason too short
        await Bad(Proposal([jpy], [b9])); // currency mismatch
        await Bad(Proposal([b9], [b9])); // wrong side / duplicate
        await Bad(Proposal(["TRF-99999999"], [b9])); // not in the run
        await Bad(Proposal(["nonsense"], [b9]));
        await Bad(new { leftTransactionIds = Array.Empty<string>(), rightTransactionIds = new[] { b9 }, reason = "Valid reason" });
        Assert.NotEqual("", gbp);
        Assert.Equal(before, await Count("SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_PROPOSED'"));
    }

    [Fact]
    public async Task Propose_with_record_in_matched_or_pending_group_is_409_record_already_used()
    {
        var matched = await Analyst.SendAsync(Post("/api/v1/runs/R-M1/match-groups", Proposal([await Trf("row-1")], [await Bnk("B-9")])));
        Assert.Equal(HttpStatusCode.Conflict, matched.StatusCode);
        Assert.Equal("record-already-used", Slug(await Json(matched)));

        // A record in a pending proposal is used too.
        var (_, _) = await ProposeOk(Analyst, [await Trf("row-x1")], [await Bnk("B-E1")]);
        var again = await Analyst.SendAsync(Post("/api/v1/runs/R-M1/match-groups", Proposal([await Trf("row-x2")], [await Bnk("B-E1")])));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("record-already-used", Slug(await Json(again)));
    }

    [Fact]
    public async Task Propose_permissions_scope_and_idempotency()
    {
        var body = Proposal([await Trf("row-x3")], [await Bnk("B-E3")]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.SendAsync(Post("/api/v1/runs/R-M1/match-groups", body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Factory.As(null).SendAsync(Post("/api/v1/runs/R-M1/match-groups", body))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Analyst.SendAsync(Post("/api/v1/runs/R-nope/match-groups", body))).StatusCode);

        var key = Guid.NewGuid().ToString();
        var first = await Analyst.SendAsync(Post("/api/v1/runs/R-M1/match-groups", body, key: key));
        var replay = await Analyst.SendAsync(Post("/api/v1/runs/R-M1/match-groups", body, key: key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal((await Json(first)).GetProperty("id").GetString(), (await Json(replay)).GetProperty("id").GetString());
        Assert.Equal(1, await Count($"SELECT count(*) FROM approvals WHERE object_type = 'MANUAL_MATCH' AND object_id = '{(await Json(first)).GetProperty("id").GetString()}'"));
    }

    [Fact]
    public async Task Concurrent_proposals_for_the_same_record_one_wins()
    {
        var left = await Trf("row-x4");
        var rs = await Task.WhenAll(
            Analyst.SendAsync(Post("/api/v1/runs/R-M1/match-groups", Proposal([left], [await Bnk("B-E4")]))),
            Approver.SendAsync(Post("/api/v1/runs/R-M1/match-groups", Proposal([left], [await Bnk("B-E5")]))));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], rs.Select(r => r.StatusCode).Order().ToArray());
    }

    [Fact]
    public async Task Approve_matches_the_group_consumes_sources_clears_exceptions_and_audits()
    {
        var (g, etag) = await ProposeOk(Analyst, [await Trf("row-5")], [await Bnk("B-9")]);
        // EX-X is linked to G-005 (the source group of row-5); it is still open while the proposal is pending.
        Assert.Equal("OPEN", await Id("SELECT status FROM exceptions WHERE id = 'EX-X'"));

        var res = await Decide(Approver, g, "APPROVE", etag);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ap = await Json(res);
        Assert.Equal("APPROVED", ap.GetProperty("status").GetString());
        Assert.Equal("APPROVE", ap.GetProperty("decision").GetString());
        Assert.Equal("MANUAL_MATCH", ap.GetProperty("objectType").GetString());
        Assert.NotEqual(ap.GetProperty("proposer").GetProperty("id").GetString(), ap.GetProperty("approver").GetProperty("id").GetString());
        Assert.Equal("\"v2\"", res.Headers.ETag!.Tag);

        var grp = (await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?status=MATCHED"))).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == g);
        Assert.Equal("manual", grp.GetProperty("ruleVersion").GetString());
        Assert.Equal("1:1", grp.GetProperty("cardinality").GetString());
        Assert.Contains("EX-X", grp.GetProperty("exceptionIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("RESOLVED", await Id("SELECT status FROM exceptions WHERE id = 'EX-X'"));
        Assert.Equal(0, await Count("SELECT count(*) FROM match_groups WHERE id = 'G-005'")); // emptied source group removed
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_DECIDED' AND subject_id = '{g}' AND reason = 'Reviewed the evidence'"));

        // Decided: a second decision is a state conflict even with the fresh ETag.
        var again = await Decide(Approver, g, "APPROVE", res.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid-state", Slug(await Json(again)));
    }

    [Fact]
    public async Task Reject_restores_previous_state_and_keeps_exceptions_open()
    {
        var (g, etag) = await ProposeOk(Analyst, [await Trf("row-2")], [await Bnk("B-E2")]);
        Assert.Equal("PENDING_APPROVAL", await Id($"SELECT status FROM match_groups WHERE id = '{g}'"));
        var res = await Decide(Approver, g, "REJECT", etag, "Amounts belong to different customers");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("REJECTED", (await Json(res)).GetProperty("status").GetString());
        Assert.Equal(0, await Count($"SELECT count(*) FROM match_groups WHERE id = '{g}'"));
        Assert.Equal("UNMATCHED", await Id("SELECT status FROM match_groups WHERE id = 'G-002'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM match_candidates WHERE match_group_id = 'G-002'"));
        Assert.Equal("OPEN", await Id("SELECT status FROM exceptions WHERE id = 'EX-1'")); // cleared ONLY on approval
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_DECIDED' AND subject_id = '{g}'"));
        // The records are free again.
        await ProposeOk(Analyst, [await Trf("row-2")], [await Bnk("B-E2")]);
    }

    [Fact]
    public async Task Decide_enforces_separation_of_duties_etag_permission_and_validation()
    {
        var (g, etag) = await ProposeOk(Approver, [await Trf("row-x5")], [await Bnk("B-E6")]);
        var url = $"/api/v1/runs/R-M1/match-groups/{g}/decision";
        var auditBefore = await Count("SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_DECIDED'");

        var sod = await Decide(Approver, g, "APPROVE", etag); // same user proposed
        Assert.Equal(HttpStatusCode.Forbidden, sod.StatusCode);
        Assert.Equal("separation-of-duties", Slug(await Json(sod)));
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(Analyst, g, "APPROVE", etag)).StatusCode); // no exception.approve
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(Viewer, g, "APPROVE", etag)).StatusCode);

        var other = f.Factory.As(await TokenFor("appr2", (Role.Approver, "LE-M1")));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Decide(other, g, "APPROVE", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Decide(other, g, "APPROVE", "\"v9\"")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Decide(other, g, "APPROVE", etag, "no")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Decide(other, g, "MAYBE", etag)).StatusCode);
        var stale = await other.SendAsync(Post(url, new { decision = "APPROVE", reason = "Looks right", proposalVersion = 7 }, etag));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale-proposal", Slug(await Json(stale)));

        // Out of scope: existence hidden; unknown group: 404.
        var scoped = f.Factory.As(await TokenFor("appr-m2", (Role.Approver, "LE-M2")));
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(scoped, g, "APPROVE", etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(other, "G-nope", "APPROVE", etag)).StatusCode);
        Assert.Equal(auditBefore, await Count("SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_DECIDED'"));
        Assert.Equal("PENDING_APPROVAL", await Id($"SELECT status FROM match_groups WHERE id = '{g}'"));

        // Another approver can decide; replaying the same key returns the stored response without a second audit event.
        var key = Guid.NewGuid().ToString();
        var ok = await Decide(other, g, "APPROVE", etag, key: key);
        var replay = await Decide(other, g, "APPROVE", etag, key: key);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, await Count($"SELECT count(*) FROM audit_events WHERE event_type = 'MANUAL_MATCH_DECIDED' AND subject_id = '{g}'"));
    }

    [Fact]
    public async Task Decide_on_group_that_is_not_pending_is_409_and_seeded_pending_group_is_decidable()
    {
        var matched = await Decide(Approver, "G-001", "APPROVE", "\"v1\"");
        Assert.Equal(HttpStatusCode.Conflict, matched.StatusCode);
        Assert.Equal("invalid-state", Slug(await Json(matched)));

        var wb = await Json(await Approver.GetAsync("/api/v1/runs/R-M1/match-groups?status=PENDING_APPROVAL"));
        var g4 = wb.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "G-004");
        Assert.True(g4.GetProperty("approval").GetProperty("canDecide").GetBoolean());
        Assert.Equal(["APPROVE_MANUAL_MATCH", "REJECT_MANUAL_MATCH"], g4.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Equal("\"v1\"", g4.GetProperty("etag").GetString());
        var dec = await Decide(Approver, "G-004", "REJECT", g4.GetProperty("etag").GetString());
        Assert.True(dec.StatusCode == HttpStatusCode.OK, await dec.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("SIGNED_OFF", "invalid-state")]
    [InlineData("SUPERSEDED", "run-superseded")]
    public async Task Proposing_on_a_locked_run_is_409_with_the_matching_problem_type(string state, string slug)
    {
        var run = "R-LOCK-" + state;
        await using (var c = f.Db.CreateCommand($"INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('{run}','LE-M1','2026-04-0{(state == "SIGNED_OFF" ? 1 : 2)}',1,'rs-1','{state}')"))
        {
            await c.ExecuteNonQueryAsync();
        }
        var res = await Analyst.SendAsync(Post($"/api/v1/runs/{run}/match-groups", Proposal(["TRF-1"], ["BNK-1"])));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(slug, Slug(await Json(res)));
    }

    [Fact]
    public async Task Failed_decision_in_transaction_leaves_no_audit_event_and_no_change()
    {
        var (g, etag) = await ProposeOk(Analyst, [await Trf("row-x7")], [await Bnk("B-E7")]);
        var before = await Count("SELECT count(*) FROM audit_events");
        var res = await Decide(Approver, g, "APPROVE", "\"v5\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, res.StatusCode);
        Assert.Equal(before, await Count("SELECT count(*) FROM audit_events"));
        Assert.Equal("PENDING", await Id($"SELECT status FROM approvals WHERE object_id = '{g}'"));
        Assert.Equal("\"v1\"", etag);
    }

    private async Task<string> TokenFor(string key, params (Role Role, string Le)[] roles)
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true,
            roles.Select(r => new RoleAssignment(WireNames.ToUpperSnake(r.Role), r.Le)).ToList()));
        return f.Factory.Tokens.Issue("sub-" + key);
    }
}
