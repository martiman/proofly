using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Jobs;

/// <summary>PostgreSQL implementation of the queue and the worker-side lease operations. Uses the database clock.</summary>
public sealed class JobStore(NpgsqlDataSource dataSource) : IJobQueue
{
    public async Task<EnqueueResult> EnqueueAsync(JobRequest r, NpgsqlTransaction? transaction = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Type)) throw new ArgumentException("Job type is required.", nameof(r));
        if (r.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(r), "MaxAttempts must be >= 1.");

        const string insert = """
            INSERT INTO jobs (type, payload, max_attempts, run_after, dedupe_key, run_id, delivery_id)
            VALUES (@type, @payload, @max, COALESCE(@after, now()), @dedupe, @run, @delivery)
            ON CONFLICT (dedupe_key) DO NOTHING
            RETURNING id
            """;
        await using var owned = transaction is null ? await dataSource.OpenConnectionAsync(ct) : null;
        var conn = transaction?.Connection ?? owned!;
        await using var cmd = new NpgsqlCommand(insert, conn, transaction);
        cmd.Parameters.AddWithValue("type", r.Type);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(r.Payload ?? new { }, JobJson.Options) });
        cmd.Parameters.AddWithValue("max", r.MaxAttempts);
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.TimestampTz) { Value = (object?)r.RunAfter?.ToUniversalTime() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("dedupe", NpgsqlDbType.Text) { Value = (object?)r.DedupeKey ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("run", NpgsqlDbType.Text) { Value = (object?)r.RunId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("delivery", NpgsqlDbType.Text) { Value = (object?)r.DeliveryId ?? DBNull.Value });
        if (await cmd.ExecuteScalarAsync(ct) is long id) return new EnqueueResult(id, true);

        await using var sel = new NpgsqlCommand("SELECT id FROM jobs WHERE dedupe_key = @d", conn, transaction);
        sel.Parameters.AddWithValue("d", r.DedupeKey!);
        return new EnqueueResult((long)(await sel.ExecuteScalarAsync(ct))!, false);
    }

    public async Task<IReadOnlyDictionary<string, long>> CountsByStateAsync(CancellationToken ct = default)
    {
        var result = JobState.All.ToDictionary(s => s, _ => 0L);
        await using var cmd = dataSource.CreateCommand("SELECT state, count(*) FROM jobs GROUP BY state");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) result[rd.GetString(0)] = rd.GetInt64(1);
        return result;
    }

    /// <summary>Jobs RUNNING with an expired lease go back to QUEUED (or DEAD if attempts are exhausted). Returns rows touched.</summary>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE jobs SET state = CASE WHEN attempts >= max_attempts THEN 'DEAD' ELSE 'QUEUED' END,
                   locked_by = NULL, locked_until = NULL, last_error = 'Lease expired (worker lost)', updated_at = now()
            WHERE state = 'RUNNING' AND locked_until < now()
            """);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Atomically claim one ready job (FOR UPDATE SKIP LOCKED) and lease it to <paramref name="worker"/>.</summary>
    public async Task<ClaimedJob?> ClaimAsync(string worker, TimeSpan lease, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE jobs SET state = 'RUNNING', attempts = attempts + 1, locked_by = @w,
                   locked_until = now() + make_interval(secs => @lease), updated_at = now()
            WHERE id = (SELECT id FROM jobs WHERE state IN ('QUEUED','FAILED') AND run_after <= now()
                        ORDER BY run_after, id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id, type, payload::text, attempts, max_attempts, run_id, delivery_id
            """);
        cmd.Parameters.AddWithValue("w", worker);
        cmd.Parameters.AddWithValue("lease", lease.TotalSeconds);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new ClaimedJob(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4),
            rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6));
    }

    /// <summary>Extend the lease. False means the lease was lost (job re-queued or taken by another worker).</summary>
    public Task<bool> HeartbeatAsync(long id, string worker, TimeSpan lease, CancellationToken ct = default) =>
        Fenced("UPDATE jobs SET locked_until = now() + make_interval(secs => @lease), updated_at = now() WHERE id=@id AND locked_by=@w AND state='RUNNING'",
            id, worker, lease, null, ct);

    public Task<bool> CompleteAsync(long id, string worker, CancellationToken ct = default) =>
        Fenced("UPDATE jobs SET state='SUCCEEDED', locked_by=NULL, locked_until=NULL, last_error=NULL, updated_at=now() WHERE id=@id AND locked_by=@w AND state='RUNNING'",
            id, worker, null, null, ct);

    /// <summary>Record a failed attempt: FAILED with backoff, or DEAD when attempts are exhausted or <paramref name="permanent"/>. Returns the new state or null if the lease was lost.</summary>
    public async Task<string?> FailAsync(long id, string worker, string error, TimeSpan backoff, bool permanent, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE jobs SET state = CASE WHEN @perm OR attempts >= max_attempts THEN 'DEAD' ELSE 'FAILED' END,
                   run_after = now() + make_interval(secs => @backoff), locked_by = NULL, locked_until = NULL,
                   last_error = @err, updated_at = now()
            WHERE id=@id AND locked_by=@w AND state='RUNNING' RETURNING state
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("w", worker);
        cmd.Parameters.AddWithValue("perm", permanent);
        cmd.Parameters.AddWithValue("backoff", backoff.TotalSeconds);
        cmd.Parameters.AddWithValue("err", error.Length > 4000 ? error[..4000] : error);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Graceful shutdown: hand the job back without consuming an attempt.</summary>
    public Task<bool> ReleaseAsync(long id, string worker, CancellationToken ct = default) =>
        Fenced("UPDATE jobs SET state='QUEUED', attempts=attempts-1, locked_by=NULL, locked_until=NULL, updated_at=now() WHERE id=@id AND locked_by=@w AND state='RUNNING'",
            id, worker, null, null, ct);

    private async Task<bool> Fenced(string sql, long id, string worker, TimeSpan? lease, object? _, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("w", worker);
        if (lease is { } l) cmd.Parameters.AddWithValue("lease", l.TotalSeconds);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
