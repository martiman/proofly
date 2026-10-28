using Npgsql;
using Proofly.Data;

namespace Proofly.Api.Observability;

public sealed record ReadinessResult(string Name, bool Ok, string? Detail = null);

public interface IReadinessCheck
{
    string Name { get; }
    Task<ReadinessResult> CheckAsync(CancellationToken ct);
}

public sealed class DatabaseReadiness(NpgsqlDataSource db) : IReadinessCheck
{
    public string Name => "database";
    public async Task<ReadinessResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return new(Name, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(Name, false, "PostgreSQL is not reachable (" + ex.GetType().Name + ")");
        }
    }
}

public sealed class MigrationsReadiness(NpgsqlDataSource db) : IReadinessCheck
{
    public string Name => "migrations";
    public async Task<ReadinessResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var cmd = new NpgsqlCommand("SELECT id FROM schema_migrations", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) { applied.Add(r.GetString(0)); }
            }
            var pending = EmbeddedMigrations.Load().Select(m => m.Id).Where(id => !applied.Contains(id)).ToList();
            return pending.Count == 0 ? new(Name, true) : new(Name, false, $"{pending.Count} pending migration(s): {string.Join(", ", pending)}");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new(Name, false, "schema_migrations does not exist; run 'Proofly.Worker migrate'");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(Name, false, "migration state unavailable (" + ex.GetType().Name + ")");
        }
    }
}

public sealed class EvidenceDirectoryReadiness(IConfiguration configuration) : IReadinessCheck
{
    public string Name => "evidence";
    public async Task<ReadinessResult> CheckAsync(CancellationToken ct)
    {
        var dir = configuration[StartupSelfCheck.EvidenceKey];
        if (string.IsNullOrWhiteSpace(dir)) { return new(Name, false, $"{StartupSelfCheck.EvidenceKey} is not configured"); }
        if (!Directory.Exists(dir)) { return new(Name, false, "evidence directory does not exist"); }
        var probe = Path.Combine(dir, $".ready-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
            return new(Name, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(Name, false, "evidence directory is not writable");
        }
    }
}
