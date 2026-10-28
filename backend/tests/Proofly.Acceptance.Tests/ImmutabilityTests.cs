using Proofly.Acceptance.Tests.Support;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// Scenarios 5 and 6: a run cannot be signed off with an unresolved blocking control (covered in the cycle test and here for the stale case),
/// a signed run cannot be changed by any command, and it stays untouched when a reprocess creates the next version.
/// </summary>
public sealed class ImmutabilityTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    private (string, string)[] RunTables(string run) =>
    [
        ("runs", $"id='{run}'"), ("exceptions", $"run_id='{run}'"), ("approvals", $"run_id='{run}'"), ("run_signoffs", $"run_id='{run}'"), ("safeguarding_positions", $"run_id='{run}'"),
        ("safeguarding_adjustments", $"run_id='{run}'"), ("match_groups", $"run_id='{run}'"), ("run_control_results", $"run_id='{run}'"), ("audit_events", $"run_id='{run}'"),
        ("resolution_proposals", $"exception_id IN (SELECT id FROM exceptions WHERE run_id='{run}')"), ("exception_timeline", $"exception_id IN (SELECT id FROM exceptions WHERE run_id='{run}')"),
        ("exception_notes", $"exception_id IN (SELECT id FROM exceptions WHERE run_id='{run}')"), ("jobs", "true"),
    ];

    [Fact]
    public async Task Signed_off_run_rejects_every_change_and_stays_byte_for_byte_the_same()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        await _w.ClearBlockers(run);
        // Leave things in flight that a signed run must not be able to continue: a pending adjustment, a pending match, a pending resolution.
        var openEx = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        (await _w.Propose("analyst1", openEx)).Is(200);
        var pendingProposal = await _w.PendingProposalId(openEx);
        var adj = (await _w.ProposeAdjustment("analyst1", run)).Is(201).Str("id");
        var group = (await _w.ProposeMatch("analyst1", run)).Is(201).Str("id");
        var resolved = (await _w.Strings($"SELECT id FROM exceptions WHERE run_id='{run}' AND status IN ('RESOLVED','ACCEPTED_DIFFERENCE')")).First();
        var open = (await _w.Strings($"SELECT id FROM exceptions WHERE run_id='{run}' AND status='OPEN' ORDER BY id")).First();
        await _w.EvidencePack("approver1", run);
        (await _w.Submit("analyst1", run)).Is(200);
        (await _w.SignOff("approver2", run)).Is(200);
        Assert.Equal("SIGNED_OFF", await _w.Scalar<string>($"SELECT state FROM runs WHERE id='{run}'"));

        var before = await _w.Snapshot(RunTables(run));
        var auditBefore = await _w.Count("SELECT count(*) FROM audit_events");
        var (signPack, runEtag) = (await _w.ETag("approver2", $"/api/v1/runs/{run}/sign-off"), await _w.ETag("approver2", $"/api/v1/runs/{run}"));

        var attempts = new List<(string Name, Resp Res)>
        {
            ("assign", await _w.Assign("analyst1", open, World.UserId("analyst1"))),
            ("note", await _w.Note("analyst1", open)),
            ("request evidence", await _w.Send("analyst1", HttpMethod.Post, $"/api/v1/exceptions/{open}/request-evidence", new { text = "Please send the statement" }, await _w.ETag("analyst1", $"/api/v1/exceptions/{open}"))),
            ("propose resolution on an open exception", await _w.Propose("analyst1", open)),
            ("decide a pending resolution", await _w.Send("approver1", HttpMethod.Post, $"/api/v1/exceptions/{openEx}/resolution-proposals/{pendingProposal}/decision",
                new { decision = "APPROVE", reason = "Independent review of the evidence done" }, await _w.ETag("approver1", $"/api/v1/exceptions/{openEx}"))),
            ("reopen a resolved exception", await _w.Reopen("analyst1", resolved)),
            ("propose adjustment", await _w.ProposeAdjustment("analyst1", run, amount: "5.00")),
            ("decide a pending adjustment", await _w.DecideAdjustment("approver1", adj)),
            ("propose match", await _w.ProposeMatch("analyst1", run)),
            ("decide a pending match", await _w.DecideMatch("approver1", run, group)),
            ("submit again", await _w.Send("analyst1", HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/submit", new { note = "again" }, signPack)),
            ("sign off again", await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/approve", new { attestation = true, reason = "Signing a second time" }, signPack)),
            ("return after sign-off", await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/return", new { reason = "Take it back please" }, signPack)),
        };
        foreach (var (name, res) in attempts)
        {
            Assert.True(res.Code == 409, $"{name} gave {res.Code} instead of 409: {res.RawBody}");
        }
        Assert.Equal(before, await _w.Snapshot(RunTables(run)));
        Assert.Equal(auditBefore, await _w.Count("SELECT count(*) FROM audit_events"));
        Assert.True((await _w.VerifyAudit()).IsValid);
        _ = runEtag;
    }

    [Fact]
    public async Task Reprocessing_identical_evidence_supersedes_the_signed_run_and_needs_a_new_sign_off()
    {
        await _w.Users();
        var run = await _w.SignedOffRun();
        var hash = await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'");
        var before = await _w.Snapshot(RunTables(run).Where(t => t.Item1 is not ("runs" or "jobs" or "audit_events")).ToArray());

        // Optimistic concurrency and permission still apply to a reprocess.
        (await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Because" })).Is(428);
        (await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Because" }, "\"v999\"")).Is(412);
        (await _w.Send("analyst1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Because" }, await _w.ETag("analyst1", $"/api/v1/runs/{run}"))).Is(403);
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM runs WHERE legal_entity_id='{Flow.Le}'"));

        var rid = Flow.Rid();
        var created = await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Re-run with the same evidence to confirm" }, await _w.ETag("approver1", $"/api/v1/runs/{run}"), requestId: rid);
        created.Is(202);
        await _w.Drain();

        // Same evidence, same rules: identical financial result; but the signed version is superseded and the new version is not signed.
        Assert.Equal(hash, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{Flow.RunV2}'"));
        var v1 = await _w.RunDto("viewer1", run);
        Assert.Equal("SUPERSEDED", v1.Str("state"));
        Assert.Equal("SUPERSEDED", v1.Str("signOffState"));
        var v2 = await _w.RunDto("viewer1", Flow.RunV2);
        Assert.NotEqual("SIGNED_OFF", v2.Str("signOffState"));
        Assert.Equal(0, await _w.Count($"SELECT count(*) FROM run_signoffs WHERE run_id='{Flow.RunV2}'"));
        Assert.Equal(before, await _w.Snapshot(RunTables(run).Where(t => t.Item1 is not ("runs" or "jobs" or "audit_events")).ToArray()));
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED' AND request_id='{rid}' AND run_id='{run}' AND actor_id='{World.UserId("approver1")}' AND reason IS NOT NULL"));

        // The old version can neither be reprocessed nor signed any more; only the current version can.
        var again = await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Once more please" }, await _w.ETag("approver1", $"/api/v1/runs/{run}"));
        Assert.Equal("run-superseded", again.ProblemType);
        // The new version cannot be signed off before it is submitted, and not by skipping the prepare step.
        var skip = await _w.SignOff("approver2", Flow.RunV2);
        Assert.Equal(409, skip.Code);
        Assert.NotEqual("SIGNED_OFF", await _w.Scalar<string>($"SELECT state FROM runs WHERE id='{Flow.RunV2}'"));
        Assert.True((await _w.VerifyAudit()).IsValid);
    }

    [Fact]
    public async Task Sign_off_is_stale_when_anything_changed_after_submission()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        await _w.ClearBlockers(run);
        await _w.EvidencePack("approver1", run);
        (await _w.Submit("analyst1", run, requiredApprover: World.UserId("approver2"))).Is(200);

        // Only the named approver may decide.
        Assert.Equal("forbidden", (await _w.SignOff("approver1", run)).ProblemType);

        // A non-blocking exception changes state after submission: the submitted picture is no longer what would be signed.
        var other = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        (await _w.Propose("analyst2", other)).Is(200);
        var rid = Flow.Rid();
        var stale = await _w.SignOff("approver2", run, rid);
        stale.Is(409);
        Assert.Equal("stale-signoff", stale.ProblemType);
        Assert.NotEqual("SIGNED_OFF", await _w.Scalar<string>($"SELECT state FROM runs WHERE id='{run}'"));
        Assert.Equal("PREPARATION_INCOMPLETE", await _w.Scalar<string>($"SELECT sign_off_state FROM runs WHERE id='{run}'"));
        Assert.Equal("INVALIDATED", await _w.Scalar<string>("SELECT status FROM approvals WHERE object_type='RUN_SIGN_OFF'"));
        Assert.Equal(0, await _w.Count("SELECT count(*) FROM run_signoffs WHERE state='SIGNED_OFF'"));
        Assert.Equal(1, await _w.Count($"SELECT count(*) FROM audit_events WHERE event_type='SIGN_OFF_INVALIDATED' AND request_id='{rid}' AND reason IS NOT NULL"));
        Assert.Equal(0, await _w.Count("SELECT count(*) FROM audit_events WHERE event_type='RUN_SIGNED_OFF'"));

        // Nothing is signed by accident later either: a fresh submission, then a fresh decision.
        Assert.Equal(409, (await _w.SignOff("approver2", run)).Code);
        (await _w.Submit("analyst1", run)).Is(200);
        (await _w.SignOff("approver2", run)).Is(200);
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM audit_events WHERE event_type='RUN_SIGNED_OFF'"));
    }

    [Fact]
    public async Task Returned_sign_off_goes_back_to_the_preparer_and_can_be_submitted_again()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        await _w.ClearBlockers(run);
        (await _w.Submit("analyst1", run)).Is(200);
        var returned = await _w.Return("approver1", run, Flow.Rid());
        returned.Is(200);
        Assert.Equal("RETURNED", await _w.Scalar<string>($"SELECT sign_off_state FROM runs WHERE id='{run}'"));
        (await _w.SignOff("approver2", run)).Is(409); // nothing pending any more
        (await _w.Submit("analyst2", run)).Is(200);
        (await _w.SignOff("approver1", run)).Is(200);
        Assert.Equal("SIGNED_OFF", await _w.Scalar<string>($"SELECT state FROM runs WHERE id='{run}'"));
    }
}
