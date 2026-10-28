using Npgsql;

namespace Proofly.Data.Jobs;

public interface IJobQueue
{
    /// <summary>
    /// Idempotent by <see cref="JobRequest.DedupeKey"/>. Pass the caller's transaction so the job commits
    /// or rolls back together with the business change; without one it commits on its own.
    /// </summary>
    Task<EnqueueResult> EnqueueAsync(JobRequest request, NpgsqlTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>Metrics hook: job counts by state (all states present, zero when empty).</summary>
    Task<IReadOnlyDictionary<string, long>> CountsByStateAsync(CancellationToken ct = default);
}
