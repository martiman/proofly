using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Recovery.Tests.Support;

namespace Proofly.Recovery.Tests;

/// <summary>
/// A run is processed in ONE transaction: a crash at any point leaves no partial run (no run row, no exceptions, positions,
/// results or audit events), and the retry yields the identical result hash and identical rows as an undisturbed run.
/// </summary>
public sealed class RunInterruptionTests : IAsyncLifetime
{
    private static readonly DateOnly Day = Seed.Date;
    private const string Le = "LE-REC-RUN";
    private RecoveryDb _db = null!, _reference = null!;
    private readonly string _dir = TempDir.Create("proofly-rec-run");
    private string _expectedHash = null!;
    private string _expectedShape = null!;

    // Tables a run writes to, keyed by run id.
    private static readonly string[] RunTables =
        ["runs", "run_control_results", "safeguarding_positions", "account_control_results", "exceptions", "match_groups"];

    public async Task InitializeAsync()
    {
        _db = await RecoveryDb.CreateSchemaAsync();
        _reference = await RecoveryDb.CreateSchemaAsync();
        foreach (var d in new[] { _db, _reference }) await Seed.RunInputs(d, Le);
        using var rig = new Rig(_reference, _dir);
        var o = await rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));
        _expectedHash = o.ResultHash!;
        _expectedShape = await Shape(_reference);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _reference.DisposeAsync();
        TempDir.Delete(_dir);
    }

    private static async Task<string> Shape(RecoveryDb db)
    {
        var counts = new List<string>();
        foreach (var t in RunTables) counts.Add($"{t}={await db.Count($"SELECT count(*) FROM {t}")}");
        counts.Add("rule_traces=" + await db.Count("SELECT count(*) FROM exception_rule_traces"));
        counts.Add("links=" + await db.Count("SELECT count(*) FROM exception_links"));
        counts.Add("candidates=" + await db.Count("SELECT count(*) FROM match_candidates"));
        counts.Add("audit=" + await db.Scalar<string>("SELECT string_agg(event_type||'x'||n, ',' ORDER BY event_type) FROM (SELECT event_type, count(*) n FROM audit_events GROUP BY event_type) t"));
        counts.Add("exceptions=" + await db.Scalar<string>("SELECT string_agg(id||':'||status||':'||coalesce(value_amount::text,''), ',' ORDER BY id) FROM exceptions"));
        return string.Join("\n", counts);
    }

    private async Task AssertNoTraceOfARun()
    {
        foreach (var t in RunTables) Assert.Equal(0, await _db.Count($"SELECT count(*) FROM {t}"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM exception_rule_traces"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM exception_links"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM match_candidates"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM audit_events"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM source_deliveries WHERE run_id IS NOT NULL")); // deliveries not linked to a phantom run
    }

    [Theory]
    [InlineData(1)] // RUN_STARTED, nothing else yet written but the run row
    [InlineData(2)] // first exception persisted with traces and links
    [InlineData(5)]
    [InlineData(9)] // the final run summary event, everything else already written in the open transaction
    public async Task Crash_while_processing_leaves_no_partial_run_and_the_retry_gives_the_identical_result(int failOnAudit)
    {
        using (var dying = new Rig(_db, _dir, failAuditOn: failOnAudit))
            await Assert.ThrowsAsync<InvalidOperationException>(() => dying.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day)));
        await AssertNoTraceOfARun();

        await _db.Restart();
        using var restarted = new Rig(_db, _dir);
        var o = await restarted.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));

        Assert.Equal(RunProcessingStatus.Processed, o.Status);
        Assert.Equal(_expectedHash, o.ResultHash);
        Assert.Equal(_expectedHash, await _db.Scalar<string>("SELECT result_hash FROM runs"));
        Assert.Equal(_expectedShape, await Shape(_db));
        Assert.True((await restarted.Get<IAuditVerifier>().VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Cancellation_while_processing_leaves_no_partial_run_and_the_retry_gives_the_identical_result()
    {
        using var cts = new CancellationTokenSource();
        using (var dying = new Rig(_db, _dir, failAuditOn: 4, cancelOnFail: cts))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dying.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day), cts.Token));
        await AssertNoTraceOfARun();

        using var retry = new Rig(_db, _dir);
        var o = await retry.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));
        Assert.Equal(_expectedHash, o.ResultHash);
        Assert.Equal(_expectedShape, await Shape(_db));
    }

    [Fact]
    public async Task Job_retry_after_a_failed_attempt_completes_the_run_with_the_identical_hash()
    {
        // Attempt 1 crashes inside the transaction (job goes to FAILED with backoff 0), attempt 2 on a healthy worker completes.
        using (var dying = new Rig(_db, _dir, failAuditOn: 3))
        {
            await dying.Store.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(Le, Day), DedupeKey: "run.process:retry"));
            Assert.True(await dying.PollAsync("worker-1"));
            Assert.Equal("FAILED", await _db.Scalar<string>("SELECT state FROM jobs"));
            await AssertNoTraceOfARun();
        }
        await _db.Restart();
        using var healthy = new Rig(_db, _dir);
        Assert.True(await healthy.PollAsync("worker-2"));

        Assert.Equal("SUCCEEDED", await _db.Scalar<string>("SELECT state FROM jobs"));
        Assert.Equal(2, await _db.Scalar<int>("SELECT attempts FROM jobs"));
        Assert.Equal(_expectedHash, await _db.Scalar<string>("SELECT result_hash FROM runs"));
        Assert.Equal(_expectedShape, await Shape(_db));
    }

    [Fact]
    public async Task Job_that_exhausts_its_attempts_is_dead_lettered_leaves_no_partial_run_and_a_manual_retry_completes_it()
    {
        using (var dying = new Rig(_db, _dir, failAuditOn: 3))
        {
            await dying.Store.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(Le, Day), MaxAttempts: 1, DedupeKey: "run.process:dead"));
            Assert.True(await dying.PollAsync("worker-1"));
        }
        Assert.Equal("DEAD", await _db.Scalar<string>("SELECT state FROM jobs"));
        Assert.Contains("simulated crash", await _db.Scalar<string>("SELECT last_error FROM jobs"));
        await AssertNoTraceOfARun();

        using var healthy = new Rig(_db, _dir);
        await healthy.Store.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(Le, Day), DedupeKey: "run.process:manual-retry"));
        Assert.True(await healthy.PollAsync("worker-2"));
        Assert.Equal(_expectedHash, await _db.Scalar<string>("SELECT result_hash FROM runs"));
        Assert.Equal(_expectedShape, await Shape(_db));
    }

    [Fact]
    public async Task A_processed_run_is_never_touched_again_when_its_job_is_redelivered()
    {
        using var rig = new Rig(_db, _dir);
        await rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));
        var shape = await Shape(_db);

        var again = await rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));

        Assert.Equal(RunProcessingStatus.AlreadyProcessed, again.Status);
        Assert.Equal(shape, await Shape(_db));
    }
}
