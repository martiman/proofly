using Npgsql;

namespace Proofly.Data;

/// <summary>
/// Applies ordered SQL scripts once each, recording them in schema_migrations. Each script runs in its
/// own transaction; a concurrent runner is serialised by an advisory lock.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource)
{
    public Task<int> RunAsync(CancellationToken ct = default) => RunAsync(EmbeddedMigrations.Load(), ct);

    public async Task<int> RunAsync(IEnumerable<(string Id, string Sql)> scripts, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_lock(727001)", conn)) { await lk.ExecuteNonQueryAsync(ct); }
        try
        {
        await using (var cmd = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (id text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var applied = 0;
        foreach (var (id, sql) in scripts.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE id = @id", conn, tx);
            check.Parameters.AddWithValue("id", id);
            if (await check.ExecuteScalarAsync(ct) is not null) { continue; }

            await using (var run = new NpgsqlCommand(sql, conn, tx)) { await run.ExecuteNonQueryAsync(ct); }
            await using (var rec = new NpgsqlCommand("INSERT INTO schema_migrations (id) VALUES (@id)", conn, tx))
            {
                rec.Parameters.AddWithValue("id", id);
                await rec.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            applied++;
        }
        return applied;
        }
        finally
        {
            await using var ul = new NpgsqlCommand("SELECT pg_advisory_unlock(727001)", conn);
            await ul.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
