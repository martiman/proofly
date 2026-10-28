namespace Proofly.Api.Platform.Idempotency;

/// <summary>A stored successful response, replayed verbatim for the same key and request.</summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, IReadOnlyDictionary<string, string> Headers, byte[] Body, string? RequestId);

public enum IdempotencyOutcome { Started, Replay, Mismatch, InProgress }

public sealed record IdempotencyBegin(IdempotencyOutcome Outcome, StoredResponse? Response = null);

/// <summary>
/// Storage seam for Idempotency-Key records. The in-memory implementation is for tests and single-node dev;
/// a PostgreSQL implementation (atomic insert of scope+key, 24 h expiry) replaces it once the schema lands.
/// Implementations must make <see cref="BeginAsync"/> atomic: exactly one concurrent caller gets Started.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Claims (scope, key) for a request, or reports a replay / mismatching reuse / request in flight.</summary>
    Task<IdempotencyBegin> BeginAsync(string scope, string key, string requestHash, TimeSpan ttl, CancellationToken ct);

    /// <summary>Stores the response of a claimed request so later identical requests replay it.</summary>
    Task CompleteAsync(string scope, string key, StoredResponse response, CancellationToken ct);

    /// <summary>Releases a claim without storing (failed or non-replayable response) so the client can retry with the same key.</summary>
    Task ReleaseAsync(string scope, string key, CancellationToken ct);
}

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
