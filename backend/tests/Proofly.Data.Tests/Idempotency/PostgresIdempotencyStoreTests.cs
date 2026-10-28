using Proofly.Data.Idempotency;

namespace Proofly.Data.Tests.Idempotency;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class PostgresIdempotencyStoreTests(SchemaFixture f) : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    private PostgresIdempotencyStore Store(TimeSpan? lease = null) => new(f.DataSource, _clock, lease ?? TimeSpan.FromMinutes(5));
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
    private static StoredResponse Response(string body = "{\"ok\":true}") =>
        new(200, "application/json", new Dictionary<string, string> { ["ETag"] = "\"v2\"" }, System.Text.Encoding.UTF8.GetBytes(body), "req-1");

    public async Task InitializeAsync() => await new MigrationRunner(f.DataSource).RunAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Complete_then_identical_request_replays_the_stored_response_verbatim()
    {
        var s = Store();
        Assert.Equal(IdempotencyOutcome.Started, (await s.BeginAsync("u1", "k-replay", Hash("a"), Ttl, default)).Outcome);
        await s.CompleteAsync("u1", "k-replay", Response("{\"n\":\"1.250000\"}"), default);
        var again = await s.BeginAsync("u1", "k-replay", Hash("a"), Ttl, default);
        Assert.Equal(IdempotencyOutcome.Replay, again.Outcome);
        var r = again.Response!;
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("\"v2\"", r.Headers["ETag"]);
        Assert.Equal("{\"n\":\"1.250000\"}", System.Text.Encoding.UTF8.GetString(r.Body));
        Assert.Equal("req-1", r.RequestId);
    }

    [Fact]
    public async Task Same_key_with_a_different_request_is_a_mismatch_and_scopes_are_independent()
    {
        var s = Store();
        await s.BeginAsync("u1", "k-mismatch", Hash("a"), Ttl, default);
        Assert.Equal(IdempotencyOutcome.Mismatch, (await s.BeginAsync("u1", "k-mismatch", Hash("b"), Ttl, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.Started, (await s.BeginAsync("u2", "k-mismatch", Hash("b"), Ttl, default)).Outcome);
    }

    [Fact]
    public async Task Release_frees_the_key_but_a_completed_response_is_kept()
    {
        var s = Store();
        await s.BeginAsync("u1", "k-release", Hash("a"), Ttl, default);
        await s.ReleaseAsync("u1", "k-release", default);
        Assert.Equal(IdempotencyOutcome.Started, (await s.BeginAsync("u1", "k-release", Hash("a"), Ttl, default)).Outcome);
        await s.CompleteAsync("u1", "k-release", Response(), default);
        await s.ReleaseAsync("u1", "k-release", default);
        Assert.Equal(IdempotencyOutcome.Replay, (await s.BeginAsync("u1", "k-release", Hash("a"), Ttl, default)).Outcome);
    }

    [Fact]
    public async Task A_crashed_in_flight_request_blocks_the_key_only_until_its_lease_ends()
    {
        var s = Store(TimeSpan.FromMinutes(5));
        Assert.Equal(IdempotencyOutcome.Started, (await s.BeginAsync("u1", "k-lease", Hash("a"), Ttl, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.InProgress, (await s.BeginAsync("u1", "k-lease", Hash("a"), Ttl, default)).Outcome);
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(IdempotencyOutcome.InProgress, (await s.BeginAsync("u1", "k-lease", Hash("a"), Ttl, default)).Outcome);
        _clock.Advance(TimeSpan.FromMinutes(2)); // lease over, 24 h TTL not
        Assert.Equal(IdempotencyOutcome.Started, (await s.BeginAsync("u1", "k-lease", Hash("a"), Ttl, default)).Outcome);
        Assert.Equal(IdempotencyOutcome.InProgress, (await s.BeginAsync("u1", "k-lease", Hash("a"), Ttl, default)).Outcome);
    }

    [Fact]
    public async Task A_completed_record_expires_after_the_ttl()
    {
        var s = Store();
        await s.BeginAsync("u1", "k-ttl", Hash("a"), Ttl, default);
        await s.CompleteAsync("u1", "k-ttl", Response(), default);
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(IdempotencyOutcome.Replay, (await s.BeginAsync("u1", "k-ttl", Hash("a"), Ttl, default)).Outcome);
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(IdempotencyOutcome.Started, (await s.BeginAsync("u1", "k-ttl", Hash("a"), Ttl, default)).Outcome);
    }

    [Fact]
    public async Task Two_simultaneous_identical_requests_exactly_one_executes_the_other_sees_in_progress_then_the_stored_response()
    {
        var s = Store();
        var gate = new TaskCompletionSource();
        var executed = 0;
        var begins = new List<IdempotencyBegin>();

        async Task<IdempotencyBegin> Request()
        {
            await gate.Task;
            var b = await s.BeginAsync("u1", "k-race", Hash("same"), Ttl, default);
            if (b.Outcome == IdempotencyOutcome.Started) { Interlocked.Increment(ref executed); }
            return b;
        }

        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(Request)).ToArray();
        gate.SetResult();
        begins.AddRange(await Task.WhenAll(tasks));

        Assert.Equal(1, executed);
        Assert.Equal(1, begins.Count(b => b.Outcome == IdempotencyOutcome.Started));
        Assert.Equal(11, begins.Count(b => b.Outcome == IdempotencyOutcome.InProgress)); // 409 idempotency-in-progress at the API
        Assert.DoesNotContain(begins, b => b.Outcome is IdempotencyOutcome.Replay or IdempotencyOutcome.Mismatch);

        await s.CompleteAsync("u1", "k-race", Response(), default);
        Assert.Equal(IdempotencyOutcome.Replay, (await s.BeginAsync("u1", "k-race", Hash("same"), Ttl, default)).Outcome);
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM idempotency_keys WHERE \"key\" = 'k-race'"));
    }

    [Fact]
    public async Task Concurrent_reclaim_of_an_expired_lease_has_exactly_one_winner()
    {
        var s = Store(TimeSpan.FromMinutes(1));
        await s.BeginAsync("u1", "k-reclaim", Hash("a"), Ttl, default);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => s.BeginAsync("u1", "k-reclaim", Hash("a"), Ttl, default))));
        Assert.Equal(1, outcomes.Count(o => o.Outcome == IdempotencyOutcome.Started));
        Assert.Equal(9, outcomes.Count(o => o.Outcome == IdempotencyOutcome.InProgress));
    }
}
