using Proofly.Acceptance.Tests.Support;
using Proofly.Data.Audit;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// Workflow-and-governance scenarios 1, 2, 5 and 6 of the oMVP acceptance plan, end to end: deliveries registered and ingested by the worker
/// handlers, run.process, exceptions, analyst proposals, independent approvals, adjustment, manual match, sign-off gate, preparer/approver
/// sign-off with attestation, evidence pack, corrected evidence and a reprocess that leaves the signed run untouched.
/// </summary>
public sealed class DailyControlCycleTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    private async Task AuditIsIntact(string when)
    {
        var v = await _w.VerifyAudit();
        Assert.True(v.IsValid, $"audit chain broken {when}: {v.FirstBreak}");
        Assert.True(v.EventsChecked > 0);
    }

    [Fact]
    public async Task Evidence_to_signed_off_run_to_reprocessed_version()
    {
        await _w.Users();

        // ---- Evidence: registered, ingested (one row rejected with a reason, not repaired), run processed.
        var run = await _w.ProcessedRun(Flow.Le);
        Assert.Equal(Flow.RunV1, run);
        Assert.Equal("QUALIFIED", await _w.Scalar<string>("SELECT state FROM source_deliveries WHERE source_name='transfer_instructions'"));
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM delivery_rejections WHERE reason_code='REQUIRED_FIELD_MISSING' AND row_number IS NOT NULL"));
        Assert.Equal(3, await _w.Count("SELECT count(*) FROM transfer_instructions"));
        var dto = await _w.RunDto("viewer1", run);
        Assert.Equal("REVIEW_REQUIRED", dto.Str("state"));
        Assert.Equal(1, dto["version"].GetInt32());
        Assert.Equal("PREPARATION_INCOMPLETE", dto.Str("signOffState"));
        await AuditIsIntact("after run.process");

        // ---- Exceptions exist and carry the blocking flag.
        var summary = await _w.Get("analyst1", $"/api/v1/runs/{run}/exceptions/summary");
        summary.Is(200);
        Assert.Equal(2, summary["blocking"].GetInt32());
        var blocking = await _w.BlockingIds(run);
        Assert.Equal(2, blocking.Count);
        var ledger = await _w.ExceptionId(run, "independent_ledger", "BALANCE_DIFFERENCE");
        var shortfall = await _w.ExceptionId(run, "safeguarding", "SHORTFALL");

        // ---- Created, assigned, investigated.
        Assert.Equal("OPEN", await _w.StatusOf(ledger));
        (await _w.Assign("analyst1", ledger, World.UserId("analyst1"))).Is(200);
        (await _w.Note("analyst1", ledger)).Is(200);
        var evReq = await _w.Send("analyst1", HttpMethod.Post, $"/api/v1/exceptions/{ledger}/request-evidence", new { text = "Please send the EUR statement", requestedFrom = "Bank operations" },
            await _w.ETag("analyst1", $"/api/v1/exceptions/{ledger}"));
        evReq.Is(200);
        Assert.Equal("WAITING_FOR_DATA", await _w.StatusOf(ledger));
        Assert.Equal(World.UserId("analyst1"), await _w.Scalar<string>($"SELECT owner_user_id FROM exceptions WHERE id='{ledger}'"));

        // ---- Sign-off is blocked while a blocking exception is unresolved (also while a proposal is only pending).
        var blocked = await _w.Submit("analyst1", run);
        blocked.Is(409);
        Assert.Equal("invalid-state", blocked.ProblemType);
        Assert.Contains(ledger, blocked.RawBody);
        Assert.Contains(shortfall, blocked.RawBody);
        var pack0 = await _w.Get("analyst1", $"/api/v1/runs/{run}/sign-off");
        Assert.False(pack0["canSignOff"].GetBoolean());
        Assert.Equal(2, pack0["blockers"].EnumerateArray().Count(b => b.GetString()!.StartsWith("Blocking exception")));

        // ---- Material resolution needs independent approval.
        var proposal = await _w.Propose("analyst1", ledger, "ACCEPTED_DIFFERENCE");
        proposal.Is(200);
        Assert.Equal("PENDING_APPROVAL", proposal.Str("status"));
        (await _w.Submit("analyst1", run)).Is(409); // a pending proposal does not unblock
        (await _w.DecideResolution("analyst1", ledger)).Is(403); // an analyst cannot approve at all
        var approved = await _w.DecideResolution("approver1", ledger);
        approved.Is(200);
        Assert.Equal("ACCEPTED_DIFFERENCE", approved.Str("status"));
        (await _w.Submit("analyst1", run)).Is(409); // the shortfall still blocks

        // ---- Safeguarding adjustment: proposed by an analyst, approved by an approver.
        var adj = await _w.ProposeAdjustment("analyst1", run);
        adj.Is(201);
        var adjId = adj.Str("id");
        Assert.Equal("PROPOSED", adj.Str("status"));
        var adjDecided = await _w.DecideAdjustment("approver1", adjId);
        adjDecided.Is(200);
        Assert.Equal("APPROVED", adjDecided.Str("status"));
        await _w.Resolve(shortfall, kind: "RESOLVED");

        // ---- Manual match: a second person approves; the matching exception it explains is cleared.
        var mismatch = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        Assert.Equal("OPEN", await _w.StatusOf(mismatch));
        var match = await _w.ProposeMatch("analyst1", run);
        match.Is(201);
        var groupId = match.Str("id");
        Assert.Equal("PENDING_APPROVAL", match.Str("status"));
        (await _w.DecideMatch("approver1", run, groupId)).Is(200);
        Assert.Equal("MATCHED", await _w.Scalar<string>($"SELECT status FROM match_groups WHERE id='{groupId}'"));
        Assert.NotEqual("OPEN", await _w.StatusOf(mismatch));
        await AuditIsIntact("after the resolutions");

        // ---- Evidence pack generated by the worker, downloaded (audited with actor and request id).
        var genRid = Flow.Rid();
        var packId = await _w.EvidencePack("approver1", run, genRid);
        var ready = await _w.Get("viewer1", $"/api/v1/runs/{run}/evidence-pack");
        ready.Is(200);
        Assert.Equal("GENERATED", ready.Str("status"));
        var downloadRid = Flow.Rid();
        var bytes = await _w.Download("viewer1", $"/api/v1/runs/{run}/evidence-pack/download", downloadRid);
        Assert.True(bytes.Length > 100);
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_GENERATED' AND request_id='{genRid}' AND actor_id='{World.UserId("approver1")}'"));
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_DOWNLOADED' AND request_id='{downloadRid}' AND actor_id='{World.UserId("viewer1")}'"));

        // ---- Sign-off: prepared by an analyst, approved by a different approver with the attestation.
        var submit = await _w.Submit("analyst1", run);
        submit.Is(200);
        Assert.Equal("APPROVAL_PENDING", submit["run"].GetProperty("signOffState").GetString());
        (await _w.SignOff("analyst1", run)).Is(403); // the preparer cannot approve
        (await _w.SignOff("approver2", run, attestation: false)).Is(422); // no attestation, no sign-off
        var signed = await _w.SignOff("approver2", run);
        signed.Is(200);
        Assert.Equal("SIGNED_OFF", signed["run"].GetProperty("state").GetString());
        Assert.Equal(packId, signed.Str("evidencePackId"));
        Assert.Equal((World.UserId("analyst1"), World.UserId("approver2")),
            (await _w.Scalar<string>($"SELECT prepared_by_user_id FROM run_signoffs WHERE run_id='{run}'"), await _w.Scalar<string>($"SELECT signed_by_user_id FROM run_signoffs WHERE run_id='{run}'")));
        Assert.False(string.IsNullOrEmpty(await _w.Scalar<string>($"SELECT attestation_hash FROM run_signoffs WHERE run_id='{run}'")));
        await AuditIsIntact("after sign-off");

        // ---- Corrected evidence arrives; the signed run does not move, a reprocess creates v2.
        var v1Before = await V1Snapshot(run);
        var v1Hash = await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'");
        var reg = await _w.RegisterTransfers(Flow.Le, "D-LE-ACC-A-transfers-corrected", correctionOf: "D-LE-ACC-A-transfer_instructions");
        Assert.True(reg.Created, string.Join(";", reg.Failures.Select(f => f.Message)));
        await _w.Drain();
        Assert.Equal("SIGNED_OFF", await _w.Scalar<string>($"SELECT state FROM runs WHERE id='{run}'"));
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM runs WHERE legal_entity_id='{Flow.Le}'")); // run.process never changes a processed run by itself

        var reprocess = await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Corrected transfer file received" }, await _w.ETag("approver1", $"/api/v1/runs/{run}"));
        reprocess.Is(202);
        Assert.Equal(Flow.RunV2, reprocess.Str("id"));
        Assert.Equal(2, reprocess["version"].GetInt32());
        await _w.Drain();

        var v1 = await _w.RunDto("viewer1", run);
        Assert.Equal("SUPERSEDED", v1.Str("state"));
        Assert.Equal("INVALIDATED", v1.Str("signOffState"));
        Assert.Equal(Flow.RunV2, v1.Str("supersededByRunId"));
        Assert.Equal(v1Hash, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'"));
        Assert.Equal(v1Before, await V1Snapshot(run));
        Assert.Equal("SIGNED_OFF", await _w.Scalar<string>($"SELECT state FROM run_signoffs WHERE run_id='{run}'")); // the sign-off record itself is kept as it was

        // v1 can no longer be changed in any way.
        var someOpen = (await _w.Strings($"SELECT id FROM exceptions WHERE run_id='{run}' AND status='OPEN' ORDER BY id")).First();
        Assert.Equal("run-superseded", (await _w.Propose("analyst1", someOpen)).ProblemType);
        Assert.Equal("run-superseded", (await _w.ProposeAdjustment("analyst1", run)).ProblemType);
        Assert.Equal("run-superseded", (await _w.SignOff("approver2", run)).ProblemType);
        Assert.Equal(v1Before, await V1Snapshot(run));

        // ---- v2 needs its own sign-off.
        var v2 = await _w.RunDto("viewer1", Flow.RunV2);
        Assert.Equal(2, v2["version"].GetInt32());
        Assert.Equal(Flow.RunV1, v2.Str("supersedesRunId"));
        Assert.NotEqual("SIGNED_OFF", v2.Str("state"));
        Assert.NotEqual("SIGNED_OFF", v2.Str("signOffState"));
        Assert.NotEqual(v1Hash, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{Flow.RunV2}'"));
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM run_signoffs WHERE run_id='{Flow.RunV2}'"));
        var early = await _w.SignOff("approver2", Flow.RunV2);
        early.Is(409); // nothing was submitted for v2

        await _w.ClearBlockers(Flow.RunV2);
        await _w.EvidencePack("approver1", Flow.RunV2);
        (await _w.Submit("analyst2", Flow.RunV2)).Is(200);
        (await _w.SignOff("approver1", Flow.RunV2)).Is(200);
        Assert.Equal(2, await _w.Count("SELECT count(*) FROM run_signoffs WHERE state='SIGNED_OFF'"));
        Assert.Equal("INVALIDATED", await _w.Scalar<string>($"SELECT sign_off_state FROM runs WHERE id='{run}'"));
        await AuditIsIntact("at the end");
    }

    /// <summary>Everything that belongs to one run version (without the run row, which legitimately flips to SUPERSEDED), serialised: any change to a superseded version shows up as a difference.</summary>
    private Task<string> V1Snapshot(string run) => _w.Snapshot(
        ("exceptions", $"run_id='{run}'"), ("approvals", $"run_id='{run}'"), ("run_signoffs", $"run_id='{run}'"),
        ("safeguarding_positions", $"run_id='{run}'"), ("safeguarding_adjustments", $"run_id='{run}'"), ("match_groups", $"run_id='{run}'"),
        ("run_control_results", $"run_id='{run}'"), ("audit_events", $"run_id='{run}' AND event_type NOT IN ('RUN_REPROCESS_REQUESTED','RUN_STARTED')"));
}
