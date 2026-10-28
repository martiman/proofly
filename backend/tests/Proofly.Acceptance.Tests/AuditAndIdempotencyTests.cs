extern alias worker;
using System.Net;
using Proofly.Acceptance.Tests.Support;
using Proofly.Data.Ingestion;
using Proofly.Data.Orchestration;
using RunProcessJob = worker::Proofly.Worker.Orchestration.RunProcessJob;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// Every material decision writes exactly one audit event (actor, reason, request id) in the same transaction as the decision, and every
/// command is idempotent: a replay with the same Idempotency-Key returns the stored answer and creates no second effect or audit event.
/// </summary>
public sealed class AuditAndIdempotencyTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    private static readonly (string, string)[] Effects =
    [
        ("exceptions", "true"), ("exception_timeline", "true"), ("exception_notes", "true"), ("exception_assignments", "true"), ("resolution_proposals", "true"), ("approvals", "true"),
        ("safeguarding_adjustments", "true"), ("match_groups", "true"), ("match_candidates", "true"), ("runs", "true"), ("run_signoffs", "true"), ("evidence_packs", "true"),
        ("config_changes", "true"), ("config_rules", "true"), ("ruleset_versions", "true"), ("jobs", "true"), ("audit_events", "true"),
    ];

    private record Decision(string Name, Resp Res, string RequestId, string EventType, string Actor, string? Reason, string? RunId);

    private async Task AssertOneEvent(Decision d, int expectedEventsWithRequestId = 1)
    {
        Assert.True(d.Res.Code is >= 200 and < 300, $"{d.Name}: HTTP {d.Res.Code} {d.Res.RawBody}");
        var sameType = await _w.Count($"SELECT count(*) FROM audit_events WHERE request_id='{d.RequestId}' AND event_type='{d.EventType}'");
        Assert.True(sameType == 1, $"{d.Name}: {sameType} {d.EventType} events for request {d.RequestId}");
        var row = await _w.Strings($"SELECT actor_type||'|'||actor_id||'|'||coalesce(reason,'')||'|'||coalesce(run_id,'') FROM audit_events WHERE request_id='{d.RequestId}' AND event_type='{d.EventType}'");
        var parts = row.Single().Split('|');
        Assert.Equal("USER", parts[0]);
        Assert.Equal(World.UserId(d.Actor), parts[1]);
        if (d.Reason is not null) { Assert.Equal(d.Reason, parts[2]); }
        Assert.True(parts[2].Length >= 3, $"{d.Name}: the audit event has no reason");
        if (d.RunId is not null) { Assert.Equal(d.RunId, parts[3]); }
        Assert.Equal(expectedEventsWithRequestId, await _w.Count($"SELECT count(*) FROM audit_events WHERE request_id='{d.RequestId}'"));
    }

    [Fact]
    public async Task Every_material_decision_has_exactly_one_audit_event_with_reason_actor_and_request_id()
    {
        await _w.Users();
        await _w.SeedRule();
        var run = await _w.ProcessedRun(Flow.Le);
        var decisions = new List<Decision>();
        string R() => Flow.Rid();

        var ex = await _w.ExceptionId(run, "independent_ledger", "BALANCE_DIFFERENCE");
        var sg = await _w.ExceptionId(run, "safeguarding", "SHORTFALL");
        var mm = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");

        var rid = R(); decisions.Add(new("propose resolution", await _w.Propose("analyst1", ex, "ACCEPTED_DIFFERENCE", rid), rid, "RESOLUTION_PROPOSED", "analyst1", "Timing difference confirmed against the bank statement", run));
        rid = R(); decisions.Add(new("decide resolution", await _w.DecideResolution("approver1", ex, rid: rid), rid, "RESOLUTION_DECIDED", "approver1", "Independent review of the evidence done", run));
        rid = R(); decisions.Add(new("reopen", await _w.Reopen("analyst1", ex, rid), rid, "EXCEPTION_REOPENED", "analyst1", "New information arrived", run));
        await _w.Resolve(ex);
        rid = R(); decisions.Add(new("propose adjustment", await _w.ProposeAdjustment("analyst1", run, rid), rid, "ADJUSTMENT_PROPOSED", "analyst1", "Pending client money transfer in transit", run));
        var adj = decisions[^1].Res.Str("id");
        rid = R(); decisions.Add(new("decide adjustment", await _w.DecideAdjustment("approver1", adj, rid: rid), rid, "ADJUSTMENT_DECIDED", "approver1", "Evidence of the transfer in transit seen", run));
        await _w.Resolve(sg, kind: "RESOLVED");
        rid = R(); decisions.Add(new("propose match", await _w.ProposeMatch("analyst1", run, rid), rid, "MANUAL_MATCH_PROPOSED", "analyst1", "Bank booked a fee on top; confirmed on the statement", run));
        var group = decisions[^1].Res.Str("id");
        rid = R(); decisions.Add(new("decide match", await _w.DecideMatch("approver1", run, group, rid: rid), rid, "MANUAL_MATCH_DECIDED", "approver1", "Fee confirmed independently", run));
        _ = mm;

        rid = R(); decisions.Add(new("generate evidence pack", await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/evidence-pack", null, null, null, rid), rid, "EVIDENCE_PACK_GENERATED", "approver1", null, run));
        await _w.Drain();
        rid = R(); decisions.Add(new("submit", await _w.Submit("analyst1", run, rid: rid), rid, "SIGN_OFF_SUBMITTED", "analyst1", "All blocking matters closed", run));
        rid = R(); decisions.Add(new("return", await _w.Return("approver1", run, rid), rid, "SIGN_OFF_RETURNED", "approver1", "Please add the missing statement", run));
        (await _w.Submit("analyst1", run)).Is(200);
        rid = R(); decisions.Add(new("sign off", await _w.SignOff("approver2", run, rid), rid, "RUN_SIGNED_OFF", "approver2", "Reviewed the evidence pack and the resolutions", run));
        rid = R(); decisions.Add(new("reprocess", await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Corrected transfer file received" }, await _w.ETag("approver1", $"/api/v1/runs/{run}"), requestId: rid),
            rid, "RUN_REPROCESS_REQUESTED", "approver1", "Corrected transfer file received", run));

        var change = (await _w.ProposeConfig("cfg1", Flow.Tomorrow, rid: rid = R())).Is(201);
        decisions.Add(new("propose config", change, rid, "CONFIG_CHANGE_PROPOSED", "cfg1", "Tolerance aligned with the bank fee schedule", null));
        rid = R(); decisions.Add(new("decide config", await _w.DecideConfig("cfg2", change.Str("id"), rid: rid), rid, "CONFIG_CHANGE_DECIDED", "cfg2", "Fee schedule checked by a second administrator", null));

        foreach (var d in decisions)
        {
            // The reprocess request also writes RUN_STARTED for the new version under the same request id: two events, one of each type.
            await AssertOneEvent(d, d.Name == "reprocess" ? 2 : 1);
        }

        // Invariants over the whole log: every user event names its actor, carries a request id, and every decision a reason.
        Assert.Equal(0, await _w.Count("SELECT count(*) FROM audit_events WHERE actor_type='USER' AND (actor_id IS NULL OR request_id IS NULL)"));
        Assert.Equal(0, await _w.Count("""
            SELECT count(*) FROM audit_events WHERE actor_type='USER' AND (reason IS NULL OR length(reason) < 3)
              AND event_type IN ('RESOLUTION_PROPOSED','RESOLUTION_DECIDED','EXCEPTION_REOPENED','ADJUSTMENT_PROPOSED','ADJUSTMENT_DECIDED','MANUAL_MATCH_PROPOSED','MANUAL_MATCH_DECIDED',
                                 'SIGN_OFF_RETURNED','RUN_SIGNED_OFF','RUN_REPROCESS_REQUESTED','CONFIG_CHANGE_PROPOSED','CONFIG_CHANGE_DECIDED')
            """));
        Assert.True((await _w.VerifyAudit()).IsValid);
    }

    [Fact]
    public async Task A_refused_command_leaves_no_trace()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        var ex = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        (await _w.Propose("approver1", ex)).Is(200);
        var before = await _w.Snapshot(Effects);
        (await _w.DecideResolution("approver1", ex)).Is(403);                         // separation of duties
        (await _w.Propose("viewer1", ex)).Is(403);                                    // no permission
        (await _w.Submit("analyst1", run)).Is(409);                                   // blocked
        (await _w.SignOff("approver2", run)).Is(409);                                 // nothing submitted
        Assert.Equal(422, (await _w.Send("approver1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/notes", new { text = "" }, await _w.ETag("approver1", $"/api/v1/exceptions/{ex}"))).Code);
        Assert.Equal(412, (await _w.Send("approver1", HttpMethod.Post, $"/api/v1/exceptions/{ex}/notes", new { text = "late" }, "\"v999\"")).Code);
        Assert.Equal(before, await _w.Snapshot(Effects));
    }

    private async Task Replay(string name, Func<string, Task<Resp>> send, int expected = 200)
    {
        var key = Guid.NewGuid().ToString();
        var first = await send(key);
        Assert.True(first.Code == expected, $"{name}: first call gave {first.Code}: {first.RawBody}");
        Assert.False(first.Replayed);
        await _w.Drain();
        var after = await _w.Snapshot(Effects);
        var second = await send(key);
        Assert.True(second.Replayed, $"{name}: second call was not served as a replay ({second.Code}): {second.RawBody}");
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.RawBody, second.RawBody);
        await _w.Drain();
        Assert.True(after == await _w.Snapshot(Effects), $"{name}: the replay changed something");
    }

    [Fact]
    public async Task Replaying_any_command_with_the_same_key_creates_no_second_effect_or_audit_event()
    {
        await _w.Users();
        await _w.SeedRule();
        var run = await _w.ProcessedRun(Flow.Le);
        var ex = await _w.ExceptionId(run, "independent_ledger", "BALANCE_DIFFERENCE");
        var sg = await _w.ExceptionId(run, "safeguarding", "SHORTFALL");
        string Etag(string url) => _w.ETag("analyst1", url).GetAwaiter().GetResult();

        var exUrl = $"/api/v1/exceptions/{ex}";
        var exEtag = Etag(exUrl);
        await Replay("assign", k => _w.Send("analyst1", HttpMethod.Post, $"{exUrl}/assign", new { ownerId = World.UserId("analyst1"), note = "Taking this one" }, exEtag, k));
        exEtag = Etag(exUrl);
        await Replay("note", k => _w.Send("analyst1", HttpMethod.Post, $"{exUrl}/notes", new { text = "Checked the bank statement" }, exEtag, k));
        exEtag = Etag(exUrl);
        await Replay("request evidence", k => _w.Send("analyst1", HttpMethod.Post, $"{exUrl}/request-evidence", new { text = "Please send the statement" }, exEtag, k));
        exEtag = Etag(exUrl);
        await Replay("propose resolution", k => _w.Send("analyst1", HttpMethod.Post, $"{exUrl}/resolution-proposals",
            new { kind = "ACCEPTED_DIFFERENCE", reason = "Timing difference confirmed against the bank statement", evidenceRefs = Flow.Refs }, exEtag, k));
        var pid = await _w.PendingProposalId(ex);
        var decideEtag = Etag(exUrl);
        await Replay("decide resolution", k => _w.Send("approver1", HttpMethod.Post, $"{exUrl}/resolution-proposals/{pid}/decision",
            new { decision = "APPROVE", reason = "Independent review of the evidence done" }, decideEtag, k));
        var reopenEtag = Etag(exUrl);
        await Replay("reopen", k => _w.Send("analyst1", HttpMethod.Post, $"{exUrl}/reopen", new { reason = "New information arrived" }, reopenEtag, k));
        await _w.Resolve(ex);

        await Replay("propose adjustment", k => _w.ProposeAdjustment("analyst1", run, key: k), 201);
        var adj = await _w.Scalar<string>("SELECT id FROM safeguarding_adjustments");
        var adjEtag = await _w.VersionTag("safeguarding_adjustments", adj!);
        await Replay("decide adjustment", k => _w.Send("approver1", HttpMethod.Post, $"/api/v1/safeguarding/adjustments/{adj}/decision",
            new { decision = "APPROVE", reason = "Evidence of the transfer in transit seen" }, adjEtag, k));
        await _w.Resolve(sg, kind: "RESOLVED");

        await Replay("propose match", k => _w.ProposeMatch("analyst1", run, key: k), 201);
        var group = await _w.Scalar<string>("SELECT id FROM match_groups WHERE status='PENDING_APPROVAL'");
        var groupEtag = await _w.VersionTag("match_groups", group!);
        await Replay("decide match", k => _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/match-groups/{group}/decision",
            new { decision = "APPROVE", reason = "Fee confirmed independently" }, groupEtag, k));

        await Replay("generate evidence pack", k => _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/evidence-pack", null, null, k), 202);
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM evidence_packs"));

        var signUrl = $"/api/v1/runs/{run}/sign-off";
        var so = Etag(signUrl);
        await Replay("submit", k => _w.Send("analyst1", HttpMethod.Post, $"{signUrl}/submit", new { note = "All blocking matters closed" }, so, k));
        so = Etag(signUrl);
        await Replay("return", k => _w.Send("approver1", HttpMethod.Post, $"{signUrl}/return", new { reason = "Please add the missing statement" }, so, k));
        (await _w.Submit("analyst1", run)).Is(200);
        var pack = await _w.Get("approver2", signUrl);
        await Replay("sign off", k => _w.Send("approver2", HttpMethod.Post, $"{signUrl}/approve",
            new { attestation = true, attestationTextHash = pack.Str("attestationTextHash"), reason = "Reviewed the evidence pack and the resolutions" }, pack.ETag, k));
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM run_signoffs WHERE state='SIGNED_OFF'"));
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM audit_events WHERE event_type='RUN_SIGNED_OFF'"));

        var runEtag = Etag($"/api/v1/runs/{run}");
        await Replay("reprocess", k => _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Corrected transfer file received" }, runEtag, k), 202);
        Assert.Equal(2, await _w.Count($"SELECT count(*) FROM runs WHERE legal_entity_id='{Flow.Le}'"));
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED'"));

        string? changeId = null;
        await Replay("propose config", async k => { var r = await _w.Send("cfg1", HttpMethod.Post, "/api/v1/settings/changes", new
        {
            targetKind = "RULE", targetId = Flow.RuleId, effectiveFrom = Flow.Tomorrow, reason = "Tolerance aligned with the bank fee schedule",
            after = new { control = "matching", description = "Amount tolerance for matching", tolerance = "0.05 GBP", severity = "MEDIUM", effectiveFrom = Flow.Tomorrow },
        }, null, k); changeId ??= r.Code == 201 ? r.Str("id") : changeId; return r; }, 201);
        var changeEtag = await _w.ETag("cfg2", $"/api/v1/settings/changes/{changeId}");
        await Replay("decide config", k => _w.Send("cfg2", HttpMethod.Post, $"/api/v1/settings/changes/{changeId}/decision",
            new { decision = "APPROVE", reason = "Fee schedule checked by a second administrator" }, changeEtag, k));
        Assert.Equal(2, await _w.Count("SELECT count(*) FROM ruleset_versions"));
        Assert.True((await _w.VerifyAudit()).IsValid);
    }

    [Fact]
    public async Task Key_reuse_with_a_different_request_is_refused_and_a_repeated_decision_with_a_new_key_is_a_conflict()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        var ex = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        var other = await _w.ExceptionId(run, "matching", "NO_CANDIDATE");
        var key = Guid.NewGuid().ToString();
        (await _w.Propose("analyst1", ex, key: key)).Is(200);
        var reuse = await _w.Propose("analyst1", other, key: key);
        Assert.Equal(422, reuse.Code);
        Assert.Equal("OPEN", await _w.StatusOf(other));

        // The same decision made twice with two different keys: the second one finds nothing left to decide.
        var pid = await _w.PendingProposalId(ex);
        var url = $"/api/v1/exceptions/{ex}/resolution-proposals/{pid}/decision";
        var body = new { decision = "APPROVE", reason = "Independent review of the evidence done" };
        (await _w.Send("approver1", HttpMethod.Post, url, body, await _w.ETag("approver1", $"/api/v1/exceptions/{ex}"))).Is(200);
        var before = await _w.Snapshot(Effects);
        var again = await _w.Send("approver1", HttpMethod.Post, url, body, await _w.ETag("approver1", $"/api/v1/exceptions/{ex}"));
        Assert.Equal(409, again.Code);
        Assert.Equal(before, await _w.Snapshot(Effects));
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM audit_events WHERE event_type='RESOLUTION_DECIDED'"));
    }

    [Fact]
    public async Task Worker_handlers_are_idempotent_under_redelivery()
    {
        await _w.SeedEntity(Flow.Le);
        var reg = await _w.RegisterTransfers(Flow.Le, "D-LE-ACC-A-transfer_instructions");
        Assert.True(reg.Created);
        await _w.Drain();
        var run = Flow.RunV1;
        var before = await _w.Snapshot(Effects);
        var hash = await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'");

        // The same file registered again is recognised by its fingerprint: no second delivery, no job, no audit event.
        var again = await _w.RegisterTransfers(Flow.Le, "D-LE-ACC-A-transfer_instructions");
        Assert.False(again.Created);
        await _w.Drain();
        // At-least-once delivery: the ingest and the run handlers run again for the same payload.
        await _w.RunJob<IngestDeliveryJob, IngestDeliveryPayload>(new IngestDeliveryPayload("D-LE-ACC-A-transfer_instructions"));
        await _w.RunJob<RunProcessJob, ProcessRunPayload>(new ProcessRunPayload(Flow.Le, World.Day));
        await _w.RunJob<RunProcessJob, ProcessRunPayload>(new ProcessRunPayload(Flow.Le, World.Day, run));
        await _w.Drain();

        Assert.Equal(before, await _w.Snapshot(Effects));
        Assert.Equal(hash, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'"));
        Assert.Equal(3, await _w.Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM runs WHERE legal_entity_id='{Flow.Le}'"));
        Assert.True((await _w.VerifyAudit()).IsValid);
    }
}
