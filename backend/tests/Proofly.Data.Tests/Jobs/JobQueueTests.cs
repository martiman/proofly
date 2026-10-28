using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Proofly.Data.Jobs;

namespace Proofly.Data.Tests.Jobs;

public sealed record Ping(string Name);

public sealed class Recorder
{
    public List<string> Seen { get; } = [];
    public Func<Ping, Task>? Behaviour { get; set; }
}

public sealed class PingHandler(Recorder rec) : IJobHandler<Ping>
{
    public async Task HandleAsync(Ping p, JobContext c, CancellationToken ct)
    {
        lock (rec.Seen) rec.Seen.Add(p.Name);
        if (rec.Behaviour is { } b) await b(p);
    }
}

public class JobQueueTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    private readonly Recorder _rec = new();
    private readonly JobOptions _opt = new() { BackoffBase = TimeSpan.Zero, PollInterval = TimeSpan.FromMilliseconds(20) };
    private JobStore _store = null!;
    private ServiceProvider _sp = null!;

    public JobQueueTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync()
    {
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("TRUNCATE jobs");
        _store = new JobStore(_f.DataSource);
        var sc = new ServiceCollection();
        sc.AddSingleton(_rec);
        sc.AddSingleton(_f.DataSource);
        sc.AddJobs(o => { o.BackoffBase = _opt.BackoffBase; o.PollInterval = _opt.PollInterval; });
        sc.AddJobHandler<PingHandler, Ping>("ping");
        _sp = sc.BuildServiceProvider();
    }

    public Task DisposeAsync() => _sp.DisposeAsync().AsTask();

    private JobProcessor Processor(JobOptions? o = null) =>
        new(_store, _sp.GetRequiredService<JobHandlerRegistry>(), _sp.GetRequiredService<IServiceScopeFactory>(), o ?? _opt, NullLogger<JobProcessor>.Instance);

    private Task<string?> State(long id) => _f.Scalar<string>($"SELECT state FROM jobs WHERE id={id}");

    [Fact]
    public async Task Enqueue_is_idempotent_by_dedupe_key()
    {
        var a = await _store.EnqueueAsync(new JobRequest("ping", new Ping("a"), DedupeKey: "k1"));
        var b = await _store.EnqueueAsync(new JobRequest("ping", new Ping("b"), DedupeKey: "k1"));
        Assert.True(a.Created);
        Assert.False(b.Created);
        Assert.Equal(a.JobId, b.JobId);
        Assert.Equal(1, await _f.Scalar<long>("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Enqueue_rolls_back_with_the_business_transaction()
    {
        await using (var conn = await _f.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await _store.EnqueueAsync(new JobRequest("ping", new Ping("x"), DedupeKey: "rb"), tx);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await _f.Scalar<long>("SELECT count(*) FROM jobs"));

        await using (var conn = await _f.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await _store.EnqueueAsync(new JobRequest("ping", new Ping("x"), DedupeKey: "rb"), tx);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await _f.Scalar<long>("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Job_runs_and_succeeds()
    {
        var e = await _store.EnqueueAsync(new JobRequest("ping", new Ping("hello"), RunId: "R1"));
        Assert.True(await Processor().ProcessOneAsync("w1", default));
        Assert.False(await Processor().ProcessOneAsync("w1", default));
        Assert.Equal(["hello"], _rec.Seen);
        Assert.Equal("SUCCEEDED", await State(e.JobId));
        var counts = await _store.CountsByStateAsync();
        Assert.Equal(1, counts["SUCCEEDED"]);
        Assert.Equal(0, counts["DEAD"]);
    }

    [Fact]
    public async Task Competing_workers_never_run_the_same_job()
    {
        for (var i = 0; i < 40; i++) await _store.EnqueueAsync(new JobRequest("ping", new Ping("j" + i)));
        _rec.Behaviour = _ => Task.Delay(5);
        var workers = Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
        {
            var p = Processor();
            while (await p.ProcessOneAsync("w" + w, default)) { }
        })).ToArray();
        await Task.WhenAll(workers);
        Assert.Equal(40, _rec.Seen.Count);
        Assert.Equal(40, _rec.Seen.Distinct().Count());
        Assert.Equal(40, await _f.Scalar<long>("SELECT count(*) FROM jobs WHERE state='SUCCEEDED' AND attempts=1"));
    }

    [Fact]
    public async Task Expired_lease_is_recovered_after_a_crash()
    {
        var e = await _store.EnqueueAsync(new JobRequest("ping", new Ping("crash")));
        var claimed = await _store.ClaimAsync("dead-worker", TimeSpan.FromMinutes(1));
        Assert.Equal(e.JobId, claimed!.Id);
        Assert.Null(await _store.ClaimAsync("other", TimeSpan.FromMinutes(1)));
        Assert.Equal(0, await _store.RecoverExpiredLeasesAsync());   // lease still valid

        await _f.Exec($"UPDATE jobs SET locked_until = now() - interval '1 second' WHERE id={e.JobId}");
        Assert.True(await Processor().ProcessOneAsync("w2", default));   // recovers, then runs
        Assert.Equal("SUCCEEDED", await State(e.JobId));
        Assert.Equal(2, await _f.Scalar<int>($"SELECT attempts FROM jobs WHERE id={e.JobId}"));
        // the crashed worker can no longer complete it
        Assert.False(await _store.CompleteAsync(e.JobId, "dead-worker"));
    }

    [Fact]
    public async Task Expired_lease_with_exhausted_attempts_is_dead()
    {
        var e = await _store.EnqueueAsync(new JobRequest("ping", new Ping("x"), MaxAttempts: 1));
        await _store.ClaimAsync("w", TimeSpan.FromMinutes(1));
        await _f.Exec("UPDATE jobs SET locked_until = now() - interval '1 second'");
        await _store.RecoverExpiredLeasesAsync();
        Assert.Equal("DEAD", await State(e.JobId));
    }

    [Fact]
    public async Task Failure_retries_with_backoff_then_dead_letters()
    {
        _rec.Behaviour = _ => throw new InvalidOperationException("boom");
        var o = new JobOptions { BackoffBase = TimeSpan.FromHours(1), BackoffMax = TimeSpan.FromHours(8) };
        var e = await _store.EnqueueAsync(new JobRequest("ping", new Ping("f"), MaxAttempts: 3));

        Assert.True(await Processor(o).ProcessOneAsync("w", default));
        Assert.Equal("FAILED", await State(e.JobId));
        Assert.Contains("boom", await _f.Scalar<string>($"SELECT last_error FROM jobs WHERE id={e.JobId}"));
        Assert.False(await Processor(o).ProcessOneAsync("w", default));   // backing off: not ready
        Assert.True(await _f.Scalar<bool>($"SELECT run_after > now() + interval '50 minutes' FROM jobs WHERE id={e.JobId}"));

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            await _f.Exec("UPDATE jobs SET run_after = now()");
            Assert.True(await Processor(o).ProcessOneAsync("w", default));
        }
        Assert.Equal("DEAD", await State(e.JobId));
        Assert.Equal(3, await _f.Scalar<int>($"SELECT attempts FROM jobs WHERE id={e.JobId}"));
        await _f.Exec("UPDATE jobs SET run_after = now()");
        Assert.False(await Processor(o).ProcessOneAsync("w", default));   // dead jobs are not retried
    }

    [Fact]
    public void Backoff_is_exponential_and_capped()
    {
        var o = new JobOptions { BackoffBase = TimeSpan.FromSeconds(5), BackoffMax = TimeSpan.FromSeconds(60) };
        Assert.Equal(TimeSpan.FromSeconds(5), o.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(10), o.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(20), o.BackoffFor(3));
        Assert.Equal(TimeSpan.FromSeconds(60), o.BackoffFor(10));
    }

    [Fact]
    public async Task Permanent_failure_and_unknown_type_are_handled()
    {
        _rec.Behaviour = _ => throw new PermanentJobException("bad input");
        var a = await _store.EnqueueAsync(new JobRequest("ping", new Ping("p")));
        var b = await _store.EnqueueAsync(new JobRequest("nope"));
        await Processor().ProcessOneAsync("w", default);
        Assert.Equal("DEAD", await State(a.JobId));
        await Processor().ProcessOneAsync("w", default);
        Assert.Equal("FAILED", await State(b.JobId));
    }

    [Fact]
    public async Task Heartbeat_extends_the_lease_while_a_handler_runs()
    {
        var o = new JobOptions { LeaseDuration = TimeSpan.FromSeconds(1.5), BackoffBase = TimeSpan.Zero };
        var e = await _store.EnqueueAsync(new JobRequest("ping", new Ping("slow")));
        _rec.Behaviour = _ => Task.Delay(TimeSpan.FromSeconds(3));
        var run = Processor(o).ProcessOneAsync("w", default);
        await Task.Delay(2200);
        Assert.Equal(0, await _store.RecoverExpiredLeasesAsync());   // would have expired without heartbeats
        await run;
        Assert.Equal("SUCCEEDED", await State(e.JobId));
        Assert.Equal(1, await _f.Scalar<int>($"SELECT attempts FROM jobs WHERE id={e.JobId}"));
    }

    [Fact]
    public async Task Shutdown_releases_the_job_without_consuming_an_attempt()
    {
        var e = await _store.EnqueueAsync(new JobRequest("ping", new Ping("s")));
        using var stop = new CancellationTokenSource();
        _rec.Behaviour = async _ => { await stop.CancelAsync(); await Task.Delay(Timeout.Infinite, stop.Token); };
        await Processor().ProcessOneAsync("w", stop.Token);
        Assert.Equal("QUEUED", await State(e.JobId));
        Assert.Equal(0, await _f.Scalar<int>($"SELECT attempts FROM jobs WHERE id={e.JobId}"));
    }
}
