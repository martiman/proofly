using Proofly.Data;
using Npgsql;

namespace Proofly.Recovery.Tests.Support;

/// <summary>
/// A throw-away PostgreSQL location on the test server (localhost:55432, trust auth): either a schema inside
/// <c>proofly_test</c> (cheap, used for most scenarios) or a whole database <c>proofly_rec_&lt;guid&gt;</c> (needed for
/// pg_dump / pg_restore). <see cref="Restart"/> drops every connection and opens a new data source on the same storage,
/// which is what a service restart looks like to the database.
/// </summary>
public sealed class RecoveryDb : IAsyncDisposable
{
    public const string Server = "Host=localhost;Port=55432;Username=proofly";
    public const string AdminDatabase = "proofly_test";

    private readonly string? _schema;
    public string Database { get; }
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    private RecoveryDb(string database, string? schema) { Database = database; _schema = schema; }

    public string ConnectionString => $"{Server};Database={Database}" + (_schema is null ? "" : $";Search Path={_schema}");

    public static async Task<RecoveryDb> CreateSchemaAsync(bool migrate = true)
    {
        var db = new RecoveryDb(AdminDatabase, "t_" + Guid.NewGuid().ToString("N"));
        await Admin($"CREATE SCHEMA \"{db._schema}\"");
        db.DataSource = NpgsqlDataSource.Create(db.ConnectionString);
        if (migrate) await new MigrationRunner(db.DataSource).RunAsync();
        return db;
    }

    /// <summary>Creates an empty database (no migrations unless asked) named proofly_rec_&lt;guid&gt;.</summary>
    public static async Task<RecoveryDb> CreateDatabaseAsync(bool migrate = true)
    {
        var db = new RecoveryDb("proofly_rec_" + Guid.NewGuid().ToString("N"), null);
        await Admin($"CREATE DATABASE \"{db.Database}\"");
        db.DataSource = NpgsqlDataSource.Create(db.ConnectionString);
        if (migrate) await new MigrationRunner(db.DataSource).RunAsync();
        return db;
    }

    /// <summary>Simulates a process restart: all pooled connections are closed, a fresh data source is opened.</summary>
    public async Task Restart()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    private static async Task Admin(string sql)
    {
        await using var admin = NpgsqlDataSource.Create($"{Server};Database={AdminDatabase}");
        await using var c = admin.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    public async Task<T?> Scalar<T>(string sql)
    {
        await using var c = DataSource.CreateCommand(sql);
        var v = await c.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    public async Task<long> Count(string sql) => await Scalar<long>(sql);

    public async Task Exec(string sql)
    {
        await using var c = DataSource.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    /// <summary>Row count of every base table in the data set (public schema, or the test schema).</summary>
    public async Task<SortedDictionary<string, long>> TableCounts()
    {
        var names = new List<string>();
        await using (var c = DataSource.CreateCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY 1"))
        await using (var r = await c.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        }
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var n in names) result[n] = await Count($"SELECT count(*) FROM \"{n}\"");
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await Admin(_schema is null
            ? $"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE)"
            : $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
    }
}
