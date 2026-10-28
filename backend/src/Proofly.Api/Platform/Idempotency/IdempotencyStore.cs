using Proofly.Data.Idempotency;

namespace Proofly.Api.Platform.Idempotency;

/// <summary>In-memory store for tests and single-node dev without a database. Production uses <see cref="PostgresIdempotencyStore"/>.</summary>
public sealed class InMemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private sealed class Entry(string hash, DateTimeOffset expires)
    {
        public string Hash { get; } = hash;
        public DateTimeOffset Expires { get; } = expires;
        public StoredResponse? Response { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<(string, string), Entry> _entries = new();

    public Task<IdempotencyBegin> BeginAsync(string scope, string key, string requestHash, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue((scope, key), out var e) && e.Expires > now)
            {
                if (e.Hash != requestHash) { return Task.FromResult(new IdempotencyBegin(IdempotencyOutcome.Mismatch)); }
                return Task.FromResult(e.Response is { } r
                    ? new IdempotencyBegin(IdempotencyOutcome.Replay, r)
                    : new IdempotencyBegin(IdempotencyOutcome.InProgress));
            }
            _entries[(scope, key)] = new Entry(requestHash, now + ttl);
            return Task.FromResult(new IdempotencyBegin(IdempotencyOutcome.Started));
        }
    }

    public Task CompleteAsync(string scope, string key, StoredResponse response, CancellationToken ct)
    {
        lock (_gate) { if (_entries.TryGetValue((scope, key), out var e)) { e.Response = response; } }
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string scope, string key, CancellationToken ct)
    {
        lock (_gate) { _entries.Remove((scope, key)); }
        return Task.CompletedTask;
    }
}
