using Proofly.Acceptance.Tests.Support;
using Proofly.Domain.Enums;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// Scenarios 4 (legal-entity scope) and viewer read-only: a user scoped to entity A sees and changes nothing of entity B, and a user with
/// a read-only role cannot mutate anything even inside the entity.
/// </summary>
public sealed class EntityScopeTests : IAsyncLifetime
{
    private readonly World _w = new();
    private string _runA = "", _runB = "";
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    private async Task Arrange()
    {
        await _w.Users();
        _runA = await _w.ProcessedRun(Flow.Le);
        _runB = await _w.ProcessedRunSeeded(Flow.LeB);
        await _w.AddUser("scoped-a", (Role.Approver, Flow.Le));       // everything in A, nothing in B
        await _w.AddUser("mixed", (Role.Approver, Flow.Le), (Role.Viewer, Flow.LeB)); // may act in A, only read in B
        await _w.AddUser("outsider", (Role.Approver, "LE-ACC-ELSEWHERE"));
        await _w.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-ACC-ELSEWHERE', 'Elsewhere', 'GBP')");
    }

    private static string Not(string text, params string[] forbidden)
    {
        foreach (var f in forbidden) { Assert.DoesNotContain(f, text); }
        return text;
    }

    /// <summary>A problem body may echo the path the caller itself asked for (instance); everything else must be free of the other entity's data.</summary>
    private static void NoLeak(Resp problem, params string[] forbidden)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(problem.RawBody)!.AsObject();
        o.Remove("instance");
        Not(o.ToJsonString(), forbidden);
    }

    [Fact]
    public async Task Scoped_user_sees_nothing_of_another_entity()
    {
        await Arrange();
        var exB = await _w.ExceptionId(_runB, "safeguarding", "SHORTFALL");
        var adjB = (await _w.ProposeAdjustment("approver1", _runB)).Is(201).Str("id");
        var packB = await _w.EvidencePack("approver1", _runB);
        var trfB = $"TRF-{await _w.Scalar<long>($"SELECT t.id FROM transfer_instructions t JOIN source_deliveries d ON d.id=t.delivery_id WHERE d.run_id='{_runB}' LIMIT 1")}";
        var deliveryB = $"D-{Flow.LeB}-transfer_instructions";
        string[] markers = [Flow.LeB, _runB, exB, adjB, deliveryB];

        // Lists contain only entity A.
        var runs = await _w.Get("scoped-a", "/api/v1/runs?includeSuperseded=true");
        runs.Is(200);
        Assert.Equal([_runA], runs.Items().Select(r => r.GetProperty("id").GetString()!).ToArray());
        Not(runs.RawBody, markers);
        var entities = await _w.Get("scoped-a", "/api/v1/legal-entities");
        Assert.Equal([Flow.Le], entities.Items().Select(e => e.GetProperty("id").GetString()!).ToArray());
        Not(entities.RawBody, markers);
        var ownExceptions = await _w.Get("scoped-a", $"/api/v1/runs/{_runA}/exceptions?limit=200");
        ownExceptions.Is(200);
        Assert.NotEmpty(ownExceptions.Items());
        Not(ownExceptions.RawBody, markers);
        foreach (var path in new[] { "/api/v1/audit-events?limit=200", "/api/v1/approvals?limit=200" })
        {
            var r = await _w.Get("scoped-a", path);
            r.Is(200);
            Not(r.RawBody, markers);
        }
        var transactions = await _w.Get("scoped-a", $"/api/v1/runs/{_runA}/transactions?limit=200");
        transactions.Is(200);
        Not(transactions.RawBody, Flow.LeB, trfB + "\"");

        // Every direct read of an entity-B resource is a 404 that discloses nothing (not 403: existence is not revealed).
        foreach (var path in new[]
        {
            $"/api/v1/runs/{_runB}", $"/api/v1/runs/{_runB}/control-room", $"/api/v1/runs/{_runB}/exceptions", $"/api/v1/runs/{_runB}/exceptions/summary",
            $"/api/v1/exceptions/{exB}", $"/api/v1/runs/{_runB}/sign-off", $"/api/v1/runs/{_runB}/evidence-pack", $"/api/v1/runs/{_runB}/evidence-pack/download",
            $"/api/v1/runs/{_runB}/match-groups", $"/api/v1/runs/{_runB}/matching/workbench", $"/api/v1/runs/{_runB}/safeguarding", $"/api/v1/runs/{_runB}/safeguarding/adjustments",
            $"/api/v1/runs/{_runB}/deliveries", $"/api/v1/deliveries/{deliveryB}", $"/api/v1/runs/{_runB}/transactions", $"/api/v1/runs/{_runB}/transactions/{trfB}", $"/api/v1/runs/{_runB}/transactions/{trfB}/lineage", $"/api/v1/runs/{_runB}/accounts", $"/api/v1/runs/{_runB}/compare?against={_runA}",
        })
        {
            var r = await _w.Get("scoped-a", path);
            Assert.True(r.Code == 404, $"GET {path} gave {r.Code}: {r.RawBody}");
            Assert.Equal("not-found", r.ProblemType);
            NoLeak(r, markers);
        }
        Assert.Equal(packB, await _w.Scalar<string>($"SELECT id FROM evidence_packs WHERE run_id='{_runB}'"));
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM audit_events WHERE event_type IN ('EVIDENCE_PACK_DOWNLOADED') AND run_id='{_runB}'"));
    }

    [Fact]
    public async Task Scoped_user_cannot_change_another_entity()
    {
        await Arrange();
        var exB = await _w.ExceptionId(_runB, "safeguarding", "SHORTFALL");
        var adjB = (await _w.ProposeAdjustment("approver1", _runB)).Is(201).Str("id");
        Assert.Equal(200, (await _w.Propose("analyst1", exB, "RESOLVED")).Code);
        var proposalB = await _w.PendingProposalId(exB);
        var groupB = (await _w.ProposeMatch("analyst1", _runB)).Is(201).Str("id");
        await _w.ClearBlockers(_runB); // entity B ends with a pending sign-off submission: the most sensitive state to leave untouched
        var packB = await _w.EvidencePack("approver1", _runB);
        (await _w.Submit("analyst1", _runB)).Is(200);

        (string, string)[] Tables() =>
        [
            ("runs", $"id='{_runB}'"), ("exceptions", $"run_id='{_runB}'"), ("approvals", $"run_id='{_runB}'"), ("safeguarding_adjustments", $"run_id='{_runB}'"),
            ("match_groups", $"run_id='{_runB}'"), ("run_signoffs", $"run_id='{_runB}'"), ("audit_events", $"run_id='{_runB}'"),
            ("resolution_proposals", $"exception_id IN (SELECT id FROM exceptions WHERE run_id='{_runB}')"), ("evidence_packs", $"run_id='{_runB}'"),
        ];
        var before = await _w.Snapshot(Tables());

        var attempts = new List<(string Name, Resp Res)>
        {
            ("assign", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/exceptions/{exB}/assign", new { ownerId = World.UserId("scoped-a") }, "*")),
            ("note", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/exceptions/{exB}/notes", new { text = "hello" }, "*")),
            ("propose resolution", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/exceptions/{exB}/resolution-proposals", new { kind = "RESOLVED", reason = "Because I say so", evidenceRefs = Flow.Refs }, "*")),
            ("decide resolution", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/exceptions/{exB}/resolution-proposals/{proposalB}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
            ("reopen", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/exceptions/{exB}/reopen", new { reason = "Reopening it" }, "*")),
            ("propose adjustment", await _w.ProposeAdjustment("scoped-a", _runB)),
            ("decide adjustment", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/safeguarding/adjustments/{adjB}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
            ("propose match", await _w.ProposeMatch("scoped-a", _runB)),
            ("decide match", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/runs/{_runB}/match-groups/{groupB}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
            ("submit sign-off", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/runs/{_runB}/sign-off/submit", new { note = "hello" }, "*")),
            ("approve sign-off", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/runs/{_runB}/sign-off/approve", new { attestation = true, reason = "Looks fine to me" }, "*")),
            ("return sign-off", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/runs/{_runB}/sign-off/return", new { reason = "Send it back please" }, "*")),
            ("reprocess", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/runs/{_runB}/reprocess", new { reason = "Reprocess it please" }, "*")),
            ("generate evidence pack", await _w.Send("scoped-a", HttpMethod.Post, $"/api/v1/runs/{_runB}/evidence-pack")),
        };
        foreach (var (name, res) in attempts)
        {
            Assert.True(res.Code == 404, $"{name} gave {res.Code}: {res.RawBody}");
            Assert.Equal("not-found", res.ProblemType);
            NoLeak(res, Flow.LeB, _runB, exB);
        }
        await _w.Drain();
        Assert.Equal(before, await _w.Snapshot(Tables()));
        Assert.Equal(packB, await _w.Scalar<string>($"SELECT id FROM evidence_packs WHERE run_id='{_runB}' ORDER BY created_at DESC LIMIT 1"));
        Assert.True((await _w.VerifyAudit()).IsValid);

        // A user whose only role is in some other entity is in the same position.
        var outsider = await _w.Send("outsider", HttpMethod.Post, $"/api/v1/exceptions/{exB}/notes", new { text = "hello" }, "*");
        Assert.Equal(404, outsider.Code);
        Assert.Equal("[]", (await _w.Get("outsider", "/api/v1/runs")).Body.GetProperty("items").GetRawText().Replace(" ", ""));
    }

    [Fact]
    public async Task A_role_is_per_entity_read_only_in_one_entity_and_approver_in_another()
    {
        await Arrange();
        var exA = await _w.ExceptionId(_runA, "matching", "VALUE_MISMATCH");
        var exB = await _w.ExceptionId(_runB, "matching", "VALUE_MISMATCH");

        // The mixed user may act in A ...
        (await _w.Propose("mixed", exA)).Is(200);
        // ... but in B the same user is only a viewer: visible, not changeable (403, not 404).
        (await _w.Get("mixed", $"/api/v1/exceptions/{exB}")).Is(200);
        var denied = await _w.Send("mixed", HttpMethod.Post, $"/api/v1/exceptions/{exB}/resolution-proposals",
            new { kind = "RESOLVED", reason = "Because I say so", evidenceRefs = Flow.Refs }, await _w.ETag("mixed", $"/api/v1/exceptions/{exB}"));
        denied.Is(403);
        Assert.Equal("OPEN", await _w.StatusOf(exB));
        (await _w.ProposeAdjustment("mixed", _runB)).Is(403);
        (await _w.ProposeMatch("mixed", _runB)).Is(403);
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM safeguarding_adjustments WHERE run_id='{_runB}'"));
    }

    [Fact]
    public async Task Viewer_can_read_but_not_mutate_anything()
    {
        await Arrange();
        var run = _runA;
        var ex = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        await _w.Propose("analyst1", ex);
        var proposal = await _w.PendingProposalId(ex);
        var adj = (await _w.ProposeAdjustment("analyst1", run)).Is(201).Str("id");
        var group = (await _w.ProposeMatch("analyst1", run)).Is(201).Str("id");
        await _w.SeedRule();
        var change = (await _w.ProposeConfig("cfg1", Flow.Today)).Is(201).Str("id");

        // Reading is allowed.
        foreach (var path in new[] { $"/api/v1/runs/{run}", $"/api/v1/runs/{run}/exceptions", $"/api/v1/exceptions/{ex}", $"/api/v1/runs/{run}/sign-off", $"/api/v1/runs/{run}/match-groups", $"/api/v1/runs/{run}/safeguarding" })
        {
            (await _w.Get("viewer1", path)).Is(200);
        }

        var tables = new[] { ("exceptions", "true"), ("approvals", "true"), ("resolution_proposals", "true"), ("safeguarding_adjustments", "true"), ("match_groups", "true"),
            ("run_signoffs", "true"), ("runs", "true"), ("config_changes", "true"), ("config_rules", "true"), ("ruleset_versions", "true"), ("audit_events", "true"), ("jobs", "true") };
        var before = await _w.Snapshot(tables);

        var attempts = new List<(string Name, Resp Res)>
        {
            ("assign", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/assign", new { ownerId = World.UserId("viewer1") }, "*")),
            ("note", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/notes", new { text = "hello" }, "*")),
            ("request evidence", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/request-evidence", new { text = "please send it" }, "*")),
            ("propose resolution", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/resolution-proposals", new { kind = "RESOLVED", reason = "Because I say so", evidenceRefs = Flow.Refs }, "*")),
            ("decide resolution", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/resolution-proposals/{proposal}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
            ("reopen", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/reopen", new { reason = "Reopening it" }, "*")),
            ("propose adjustment", await _w.ProposeAdjustment("viewer1", run)),
            ("decide adjustment", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/safeguarding/adjustments/{adj}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
            ("propose match", await _w.ProposeMatch("viewer1", run)),
            ("decide match", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/runs/{run}/match-groups/{group}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
            ("submit sign-off", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/submit", new { note = "hello" }, "*")),
            ("approve sign-off", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/approve", new { attestation = true, reason = "Looks fine to me" }, "*")),
            ("return sign-off", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/return", new { reason = "Send it back please" }, "*")),
            ("reprocess", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Reprocess it please" }, "*")),
            ("propose config", await _w.ProposeConfig("viewer1", Flow.Tomorrow)),
            ("decide config", await _w.Send("viewer1", HttpMethod.Post, $"/api/v1/settings/changes/{change}/decision", new { decision = "APPROVE", reason = "Looks fine to me" }, "*")),
        };
        foreach (var (name, res) in attempts)
        {
            Assert.True(res.Code == 403, $"{name} gave {res.Code}: {res.RawBody}");
            Assert.Equal("forbidden", res.ProblemType);
        }
        await _w.Drain();
        Assert.Equal(before, await _w.Snapshot(tables));
    }
}
