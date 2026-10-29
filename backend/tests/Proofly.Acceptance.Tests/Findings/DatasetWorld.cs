using Npgsql;
using Proofly.Data;
using Proofly.Data.Fixtures;

namespace Proofly.Acceptance.Tests.Findings;

/// <summary>A migrated per-test schema in proofly_test into which one dataset directory is loaded through the real registry, ingestor and run processor.</summary>
public sealed class DatasetWorld : IAsyncLifetime
{
    public const string Server = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public const string DirectoryVariable = "PROOFLY_DATASET_DIR";
    public const string SkipVariable = "PROOFLY_SKIP_DATASET_FINDINGS";
    public const string AllowListVariable = "PROOFLY_EXTRA_FINDINGS_FILE";
    public const string GapsVariable = "PROOFLY_KNOWN_GAPS_FILE";

    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    public string EvidenceDir { get; } = Path.Combine(Path.GetTempPath(), "proofly-dataset-" + Guid.NewGuid().ToString("N"));
    public NpgsqlDataSource Db { get; private set; } = null!;

    /// <summary>The dataset under test: PROOFLY_DATASET_DIR, else the committed examples/synthetic-data/2026-10-27.</summary>
    public static string DatasetDirectory => Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } d ? Path.GetFullPath(d) : CommittedDirectory;
    public static bool IsCommitted => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DirectoryVariable));

    public static string CommittedDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "examples", "synthetic-data", "2026-10-27");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("examples/synthetic-data/2026-10-27 not found above " + AppContext.BaseDirectory);
        }
    }

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Server))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Server};Search Path={Schema};Maximum Pool Size=4");
        await new MigrationRunner(Db).RunAsync();
        Directory.CreateDirectory(EvidenceDir);
    }

    public Task<DatasetLoadResult> LoadAsync(string? directory = null) =>
        SyntheticDatasetLoader.LoadAsync(Db, new DatasetLoadOptions(directory ?? DatasetDirectory, EvidenceDir));

    public async Task<List<ProducedException>> ExceptionsAsync()
    {
        var list = new List<ProducedException>();
        await using var c = Db.CreateCommand("SELECT control, reason_code, severity, source_file, source_row_id, title FROM exceptions ORDER BY control, reason_code, source_file, source_row_id, id");
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ProducedException(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        return list;
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try { Directory.Delete(EvidenceDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        await using var admin = NpgsqlDataSource.Create(Server);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }
}
