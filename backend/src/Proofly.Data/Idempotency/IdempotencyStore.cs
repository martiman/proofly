namespace Proofly.Data.Idempotency;

/// <summary>A stored successful response, replayed verbatim for the same key and request.</summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, IReadOnlyDictionary<string, string> Headers, byte[] Body, string? RequestId);

public enum IdempotencyOutcome { Started, Replay, Mismatch, InProgress }

public sealed record IdempotencyBegin(IdempotencyOutcome Outcome, StoredResponse? Response = null);

/// <summary>
/// Storage seam for Idempotency-Key records. Implementations must make <see cref="BeginAsync"/> atomic: exactly one concurrent
/// caller gets Started. A claim that is never completed or released (crashed request) becomes claimable again once its lease ends.
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
