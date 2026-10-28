using Microsoft.Extensions.DependencyInjection;
using Proofly.Data.Evidence;
using Proofly.Domain.Records;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Data.Safeguarding.Commands;
using Proofly.Recovery.Tests.Support;

namespace Proofly.Recovery.Tests;

/// <summary>
/// A worker that dies while holding a job (lease expires) must cause the job to run again on another worker, and because
/// the handlers are idempotent the business effect must exist exactly once, whether the dead worker had already
/// committed its effect or not. Death is simulated by claiming with a short lease and never completing; the stale worker's
/// late completion must be refused (fencing).
/// </summary>
public sealed class JobRecoveryTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 10, 27);
    private const string Le = "LE-REC-1";
    private RecoveryDb _db = null!;
    private Rig _rig = null!;
    private readonly string _dir = TempDir.Create("proofly-rec-job");

    public async Task InitializeAsync()
    {
        _db = await RecoveryDb.CreateSchemaAsync();
        _rig = new Rig(_db, _dir);
    }

    public async Task DisposeAsync()
    {
        _rig.Dispose();
        await _db.DisposeAsync();
        TempDir.Delete(_dir);
    }

    /// <summary>The job is claimed by "worker-dead", optionally its effect is committed, then it vanishes and the lease runs out.</summary>
    private async Task<long> DieHoldingTheJob(string type, bool effectCommittedBeforeDeath, Func<Task> effect)
    {
        var claimed = await _rig.Store.ClaimAsync("worker-dead", TimeSpan.FromMilliseconds(100));
        Assert.NotNull(claimed);
        Assert.Equal(type, claimed!.Type);
        if (effectCommittedBeforeDeath) await effect();
        await Task.Delay(400);
        return claimed.Id;
    }

    private async Task RecoverOnAnotherWorker(long jobId)
    {
        Assert.True(await _rig.PollAsync("worker-2"));
        Assert.Equal("SUCCEEDED", await _db.Scalar<string>($"SELECT state FROM jobs WHERE id={jobId}"));
        Assert.Equal(2, await _db.Scalar<int>($"SELECT attempts FROM jobs WHERE id={jobId}"));
        Assert.False(await _rig.Store.CompleteAsync(jobId, "worker-dead"), "a stale worker must not be able to complete the job");
        Assert.False(await _rig.PollAsync("worker-3"), "the job must not run a third time");
    }

    private async Task<string> ProcessedRun()
    {
        await Seed.Users(_db);
        await Seed.RunInputs(_db, Le);
        var outcome = await _rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));
        return outcome.RunId!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_job_of_a_dead_worker_runs_again_and_ingests_once(bool effectCommittedBeforeDeath)
    {
        await _db.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-CZ-001','Test','EUR')");
        var file = TransferFile.Build("2026-10-27");
        await _rig.RegisterAsync(file, "DEL-TRF-01", Day); // enqueues delivery.ingest

        var id = await DieHoldingTheJob(IngestionJobs.IngestDelivery, effectCommittedBeforeDeath,
            () => _rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01"));
        await RecoverOnAnotherWorker(id);

        Assert.Equal(10, await _db.Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM delivery_rejections"));
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='DELIVERY_RECEIVED'")); // registration + ingestion
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='RECORD_REJECTED'"));
        Assert.Equal("QUALIFIED", await _db.Scalar<string>("SELECT state FROM source_deliveries"));
    }

    [Fact]
    public async Task Last_delivery_ingested_but_worker_died_before_the_run_was_enqueued_still_gets_exactly_one_run_job()
    {
        // One mandatory source, so this delivery completes the input set. Commit of the ingestion happened, enqueue of run.process did not.
        using var rig = new Rig(_db, TempDir.Create("proofly-rec-job-enq"), mandatory: [SourceNames.TransferInstructions], runEnqueuer: true);
        try
        {
            await _db.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-CZ-001','Test','EUR')");
            await rig.RegisterAsync(TransferFile.Build("2026-10-27"), "DEL-TRF-01", Day);
            var claimed = await rig.Store.ClaimAsync("worker-dead", TimeSpan.FromMilliseconds(100));
            Assert.NotNull(claimed);
            await rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01"); // committed ...
            await Task.Delay(400);                                       // ... then the worker is gone (no run.process enqueued)
            Assert.Equal(0, await _db.Count("SELECT count(*) FROM jobs WHERE type='run.process'"));

            Assert.True(await rig.PollAsync("worker-2"));
            Assert.Equal(1, await _db.Count("SELECT count(*) FROM jobs WHERE type='run.process'"));
            Assert.Equal(1, await _db.Count("SELECT count(*) FROM jobs WHERE type='delivery.ingest' AND state='SUCCEEDED'"));
            Assert.Equal(10, await _db.Count("SELECT count(*) FROM transfer_instructions"));

            // The same delivery job redelivered once more must not add another run job.
            await rig.Get<RunEnqueuer>().OnDeliveryIngestedAsync("DEL-TRF-01");
            Assert.Equal(1, await _db.Count("SELECT count(*) FROM jobs WHERE type='run.process'"));
        }
        finally { TempDir.Delete(rig.EvidenceDir); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_process_job_of_a_dead_worker_runs_again_and_yields_one_run(bool effectCommittedBeforeDeath)
    {
        await Seed.Users(_db);
        await Seed.RunInputs(_db, Le);
        await _rig.Store.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(Le, Day), DedupeKey: "run.process:rec"));

        string? hashBefore = null;
        var id = await DieHoldingTheJob(RunJobs.ProcessRun, effectCommittedBeforeDeath, async () =>
        {
            await _rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));
            hashBefore = await _db.Scalar<string>("SELECT result_hash FROM runs");
        });
        await RecoverOnAnotherWorker(id);

        Assert.Equal(1, await _db.Count("SELECT count(*) FROM runs"));
        var hash = await _db.Scalar<string>("SELECT result_hash FROM runs");
        Assert.NotNull(hash);
        if (hashBefore is not null) Assert.Equal(hashBefore, hash);
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='RUN_STARTED'"));
        Assert.Equal(5, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='CONTROL_EXECUTED'")); // 5 controls
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='RUN_PROCESSED'")); // the run summary
        Assert.Equal(5, await _db.Count("SELECT count(*) FROM run_control_results"));
        Assert.Equal(await _db.Count("SELECT count(*) FROM exceptions"), await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='EXCEPTION_CREATED'"));
        Assert.Equal(3, await _db.Count("SELECT count(*) FROM safeguarding_positions"));
    }

    private async Task RequestPack(string runId)
    {
        var r = await _rig.Get<IEvidencePackCommands>().RequestAsync(runId, "u-prep", "req-1");
        Assert.Equal(RequestOutcome.Created, r.Outcome);
    }

    private int StoredObjects() => Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Count(f => !f.Contains($"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evidence_pack_job_of_a_dead_worker_runs_again_and_yields_one_ready_pack(bool effectCommittedBeforeDeath)
    {
        var runId = await ProcessedRun();
        await RequestPack(runId);
        var packId = (await _db.Scalar<string>("SELECT id FROM evidence_packs"))!;

        var id = await DieHoldingTheJob(EvidencePackJobs.Generate, effectCommittedBeforeDeath, async () =>
        {
            using var scope = _rig.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EvidencePackJobHandler>().HandleAsync(new GenerateEvidencePackPayload(packId), new JobContext(0, 1, 5, runId, null), default);
        });
        var shaBefore = await _db.Scalar<string>("SELECT sha256 FROM evidence_packs");
        await RecoverOnAnotherWorker(id);

        Assert.Equal("READY", await _db.Scalar<string>("SELECT status FROM evidence_packs"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM evidence_packs"));
        var sha = (await _db.Scalar<string>("SELECT sha256 FROM evidence_packs"))!.Trim();
        if (shaBefore is not null) Assert.Equal(shaBefore.Trim(), sha);
        Assert.Equal(1, StoredObjects());
        Assert.True(await _rig.Evidence.VerifyAsync(sha));
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_GENERATED'")); // requested + ready
    }

    [Fact]
    public async Task Pack_stored_but_row_still_generating_when_the_worker_died_is_completed_without_a_second_object()
    {
        var runId = await ProcessedRun();
        await RequestPack(runId);
        var packId = (await _db.Scalar<string>("SELECT id FROM evidence_packs"))!;
        var createdAt = await _db.Scalar<DateTime>("SELECT created_at FROM evidence_packs");
        // The dead worker got as far as writing the object into the store, but not as far as marking the row READY.
        var build = await _rig.Get<EvidencePackBuilder>().BuildAsync(new EvidencePackRequest(runId, "u-prep", new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc))));
        await _rig.Evidence.WriteAsync(new MemoryStream(build.Bytes));
        Assert.Equal(1, StoredObjects());
        var id = await DieHoldingTheJob(EvidencePackJobs.Generate, false, () => Task.CompletedTask);

        await RecoverOnAnotherWorker(id);

        Assert.Equal("READY", await _db.Scalar<string>("SELECT status FROM evidence_packs"));
        Assert.Equal(1, StoredObjects());
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM evidence_packs WHERE status='READY'"));
    }

    private async Task<(string Run, string A1, string A2)> TwoApprovedAdjustmentsDueForExpiry()
    {
        var runId = await ProcessedRun();
        var cmds = _rig.Get<IAdjustmentCommands>();
        var ids = new List<string>();
        foreach (var amount in new[] { 10m, 20m })
        {
            var p = await cmds.ProposeAsync(new ProposeAdjustmentCommand(runId, "u-prep", "GBP", amount, "documented timing difference", ["EV-1"], Day, null), default);
            Assert.Equal(CommandOutcome.Ok, p.Outcome);
            var d = await cmds.DecideAsync(new DecideAdjustmentCommand(p.AdjustmentId!, "u-signer", true, "approved after review", 1, 1, null), default);
            Assert.Equal(CommandOutcome.Ok, d.Outcome);
            ids.Add(p.AdjustmentId!);
        }
        return (runId, ids[0], ids[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adjustment_expiry_job_of_a_dead_worker_runs_again_and_expires_each_adjustment_once(bool effectCommittedBeforeDeath)
    {
        await TwoApprovedAdjustmentsDueForExpiry();
        await _rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-29"), DedupeKey: "expire:2026-10-29"));

        var id = await DieHoldingTheJob(AdjustmentCommands.ExpireJobType, effectCommittedBeforeDeath,
            async () => Assert.Equal(2, (await _rig.Get<IAdjustmentCommands>().ExpireAsync(new DateOnly(2026, 10, 29), default)).Expired));
        await RecoverOnAnotherWorker(id);

        Assert.Equal(2, await _db.Count("SELECT count(*) FROM safeguarding_adjustments WHERE status='EXPIRED'"));
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_EXPIRED' AND actor_type='SYSTEM'"));
    }

    [Fact]
    public async Task Adjustment_expiry_interrupted_between_adjustments_is_completed_by_the_rerun_without_double_expiry()
    {
        await TwoApprovedAdjustmentsDueForExpiry();
        // Setup used the shared rig's audit counter; use a fresh rig whose 2nd audit append fails = crash while expiring the second adjustment.
        using (var dying = new Rig(_db, _dir, failAuditOn: 2))
            await Assert.ThrowsAsync<InvalidOperationException>(() => dying.Get<IAdjustmentCommands>().ExpireAsync(new DateOnly(2026, 10, 29), default));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM safeguarding_adjustments WHERE status='EXPIRED'"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM safeguarding_adjustments WHERE status='APPROVED'"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_EXPIRED' AND actor_type='SYSTEM'"));

        await _db.Restart();
        using var restarted = new Rig(_db, _dir);
        Assert.Equal(1, (await restarted.Get<IAdjustmentCommands>().ExpireAsync(new DateOnly(2026, 10, 29), default)).Expired);
        Assert.Equal(0, (await restarted.Get<IAdjustmentCommands>().ExpireAsync(new DateOnly(2026, 10, 29), default)).Expired);

        Assert.Equal(2, await _db.Count("SELECT count(*) FROM safeguarding_adjustments WHERE status='EXPIRED'"));
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='ADJUSTMENT_EXPIRED' AND actor_type='SYSTEM'"));
    }

    [Fact]
    public async Task Worker_that_dies_on_its_last_attempt_dead_letters_the_job_and_keeps_the_failure_visible()
    {
        await _rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-29"), MaxAttempts: 2));
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.NotNull(await _rig.Store.ClaimAsync("worker-dead-" + attempt, TimeSpan.FromMilliseconds(50)));
            await Task.Delay(300);
            Assert.True(await _rig.Store.RecoverExpiredLeasesAsync() == 1);
        }
        Assert.Equal("DEAD", await _db.Scalar<string>("SELECT state FROM jobs"));
        Assert.Contains("Lease expired", await _db.Scalar<string>("SELECT last_error FROM jobs"));
        Assert.Equal(1L, (await _rig.Store.CountsByStateAsync())[JobState.Dead]);
    }
}
