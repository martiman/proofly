using ExceptionCommandOutcome = Proofly.Data.Exceptions.Commands.CommandOutcome;
using SafeguardingCommandOutcome = Proofly.Data.Safeguarding.Commands.CommandOutcome;
using System.Text;
using Proofly.Data.Exceptions.Commands;
using Proofly.Data.Idempotency;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Data.Safeguarding.Commands;
using Proofly.Recovery.Tests.Support;

namespace Proofly.Recovery.Tests;

/// <summary>
/// A service restart (every connection dropped, every in-memory object rebuilt) loses no queued work, no pending approval,
/// no saved view and no idempotency record. Durable state is read back through the same repositories the API uses.
/// </summary>
public sealed class RestartTests : IAsyncLifetime
{
    private static readonly DateOnly Day = Seed.Date;
    private RecoveryDb _db = null!;
    private readonly string _dir = TempDir.Create("proofly-rec-restart");

    public async Task InitializeAsync() => _db = await RecoveryDb.CreateSchemaAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        TempDir.Delete(_dir);
    }

    [Fact]
    public async Task Queued_failed_and_delayed_jobs_survive_a_restart_and_are_then_processed_in_order_exactly_once()
    {
        const string le = "LE-REC-Q";
        await Seed.RunInputs(_db, le);
        using (var rig = new Rig(_db, _dir))
        {
            await rig.Store.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(le, Day), DedupeKey: "k-ready"));
            await rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-29"), DedupeKey: "k-later",
                RunAfter: DateTimeOffset.UtcNow.AddHours(1)));
            // A job queued right before shutdown.
            await rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-28"), DedupeKey: "k-inflight"));
        }
        var before = await _db.Scalar<string>("SELECT string_agg(id||':'||state||':'||attempts||':'||dedupe_key, ',' ORDER BY id) FROM jobs");

        await _db.Restart();
        using var restarted = new Rig(_db, _dir);

        Assert.Equal(before, await _db.Scalar<string>("SELECT string_agg(id||':'||state||':'||attempts||':'||dedupe_key, ',' ORDER BY id) FROM jobs"));
        var counts = await restarted.Store.CountsByStateAsync();
        Assert.Equal(3, counts[JobState.Queued]);
        // Enqueueing the same business event again after the restart is still idempotent.
        Assert.False((await restarted.Store.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(le, Day), DedupeKey: "k-ready"))).Created);

        Assert.True(await restarted.PollAsync("w1")); // run.process
        Assert.True(await restarted.PollAsync("w1")); // expiry (the job delayed by one hour must still wait)
        Assert.False(await restarted.PollAsync("w1"));
        Assert.Equal(2, await _db.Count("SELECT count(*) FROM jobs WHERE state='SUCCEEDED'"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM jobs WHERE state='QUEUED' AND dedupe_key='k-later'"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM runs"));
    }

    [Fact]
    public async Task A_job_running_when_the_process_was_killed_is_taken_over_after_the_restart()
    {
        using (var rig = new Rig(_db, _dir))
        {
            await rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-29"), DedupeKey: "k"));
            Assert.NotNull(await rig.Store.ClaimAsync("worker-before-restart", TimeSpan.FromMilliseconds(100)));
        }
        await _db.Restart();
        await Task.Delay(300);
        using var restarted = new Rig(_db, _dir);

        Assert.True(await restarted.PollAsync("worker-after-restart"));
        Assert.Equal("SUCCEEDED", await _db.Scalar<string>("SELECT state FROM jobs"));
    }

    [Fact]
    public async Task Pending_approvals_survive_a_restart_and_can_still_be_decided_by_someone_else()
    {
        const string le = "LE-REC-A";
        await Seed.Users(_db);
        await Seed.RunInputs(_db, le);
        string adjustmentId, approvalId;
        using (var rig = new Rig(_db, _dir))
        {
            var o = await rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(le, Day));
            var p = await rig.Get<IAdjustmentCommands>().ProposeAsync(
                new ProposeAdjustmentCommand(o.RunId!, "u-prep", "GBP", 100m, "documented timing difference", ["EV-1"], Day, "req-1"), default);
            Assert.Equal(SafeguardingCommandOutcome.Ok, p.Outcome);
            adjustmentId = p.AdjustmentId!;
            approvalId = (await _db.Scalar<string>("SELECT id FROM approvals"))!;
        }
        var before = await _db.Scalar<string>("SELECT status||':'||proposal_version||':'||proposer_user_id||':'||required_role||':'||policy_version||':'||version FROM approvals");

        await _db.Restart();
        using var restarted = new Rig(_db, _dir);

        Assert.Equal(before, await _db.Scalar<string>("SELECT status||':'||proposal_version||':'||proposer_user_id||':'||required_role||':'||policy_version||':'||version FROM approvals"));
        Assert.Equal("PENDING", await _db.Scalar<string>($"SELECT status FROM approvals WHERE id='{approvalId}'"));
        // Separation of duties still holds after the restart, and the proper approver can decide.
        var self = await restarted.Get<IAdjustmentCommands>().DecideAsync(new DecideAdjustmentCommand(adjustmentId, "u-prep", true, "self approval", 1, 1, null), default);
        Assert.Equal(SafeguardingCommandOutcome.SeparationOfDuties, self.Outcome);
        var ok = await restarted.Get<IAdjustmentCommands>().DecideAsync(new DecideAdjustmentCommand(adjustmentId, "u-signer", true, "approved after review", 1, 1, null), default);
        Assert.Equal(SafeguardingCommandOutcome.Ok, ok.Outcome);
        Assert.Equal("APPROVED", await _db.Scalar<string>($"SELECT status FROM approvals WHERE id='{approvalId}'"));
    }

    [Fact]
    public async Task Saved_views_survive_a_restart_with_filters_sort_and_version()
    {
        await Seed.Users(_db);
        string viewId;
        using (var rig = new Rig(_db, _dir))
        {
            var (outcome, view) = await rig.Get<IExceptionCommandRepository>().CreateViewAsync("u-owner", "Open critical", """{"status":["OPEN"],"severity":["CRITICAL"]}""", "-dueAt");
            Assert.Equal(ExceptionCommandOutcome.Ok, outcome);
            viewId = view!.Id;
        }
        await _db.Restart();
        using var restarted = new Rig(_db, _dir);

        var views = await restarted.Get<IExceptionCommandRepository>().ListViewsAsync("u-owner");
        var v = Assert.Single(views);
        Assert.Equal(viewId, v.Id);
        Assert.Equal("Open critical", v.Name);
        Assert.Equal("-dueAt", v.Sort);
        Assert.Contains("CRITICAL", v.FiltersJson);
        Assert.NotNull(await restarted.Get<IExceptionCommandRepository>().FindViewAsync(viewId, "u-owner"));
        Assert.Empty(await restarted.Get<IExceptionCommandRepository>().ListViewsAsync("u-signer")); // still private to its owner
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public async Task Idempotency_keys_survive_a_restart_replay_the_stored_response_and_still_reject_a_different_request()
    {
        var ttl = TimeSpan.FromHours(24);
        var response = new StoredResponse(201, "application/json", new Dictionary<string, string> { ["ETag"] = "\"v1\"" }, Encoding.UTF8.GetBytes("{\"id\":\"ADJ-1\"}"), "req-9");
        using (var rig = new Rig(_db, _dir))
        {
            var store = rig.Get<IIdempotencyStore>();
            Assert.Equal(IdempotencyOutcome.Started, (await store.BeginAsync("u-prep", "key-done", Hash("body-1"), ttl, default)).Outcome);
            await store.CompleteAsync("u-prep", "key-done", response, default);
            Assert.Equal(IdempotencyOutcome.Started, (await store.BeginAsync("u-prep", "key-inflight", Hash("body-2"), ttl, default)).Outcome); // request in flight at shutdown
        }
        await _db.Restart();
        using var restarted = new Rig(_db, _dir);
        var after = restarted.Get<IIdempotencyStore>();

        var replay = await after.BeginAsync("u-prep", "key-done", Hash("body-1"), ttl, default);
        Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
        Assert.Equal(201, replay.Response!.StatusCode);
        Assert.Equal("{\"id\":\"ADJ-1\"}", Encoding.UTF8.GetString(replay.Response.Body));
        Assert.Equal("\"v1\"", replay.Response.Headers["ETag"]);
        Assert.Equal(IdempotencyOutcome.Mismatch, (await after.BeginAsync("u-prep", "key-done", Hash("another body"), ttl, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.InProgress, (await after.BeginAsync("u-prep", "key-inflight", Hash("body-2"), ttl, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.Started, (await after.BeginAsync("u-other", "key-done", Hash("body-1"), ttl, default)).Outcome); // keys are per caller
    }
}
