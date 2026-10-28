using Npgsql;
using Proofly.Data;

namespace Proofly.Security.Tests.Support;

/// <summary>A throw-away migrated schema in the shared proofly_test database (localhost:55432, trust auth), optionally seeded.</summary>
public class SecDb : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    public string EvidenceDir { get; } = Path.Combine(Path.GetTempPath(), "proofly-sec-" + Guid.NewGuid().ToString("N"));
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal string ConnectionString => $"{Cs};Search Path={Schema};Maximum Pool Size=6";
    protected virtual string? SeedSql => null;

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create(ConnectionString);
        await new MigrationRunner(Db).RunAsync();
        Directory.CreateDirectory(EvidenceDir);
        if (SeedSql is not null) { await Exec(SeedSql); }
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        try { Directory.Delete(EvidenceDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    public async Task Exec(string sql) { await using var c = Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }

    public async Task<T?> Scalar<T>(string sql)
    {
        await using var c = Db.CreateCommand(sql);
        var v = await c.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    /// <summary>All user-data tables serialised in a stable order: used to prove a probe changed nothing.</summary>
    public async Task<string> Snapshot(params string[] tables)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in tables)
        {
            sb.Append(t).Append('=').Append(await Scalar<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text)::text,'[]') FROM {t} x")).Append('\n');
        }
        return sb.ToString();
    }
}
