using Xunit.Abstractions;

namespace Proofly.Acceptance.Tests.Findings;

/// <summary>A fact that is skipped only when PROOFLY_SKIP_DATASET_FINDINGS=1 is set, and says so in the test report. Never skipped otherwise.</summary>
public sealed class DatasetFactAttribute : FactAttribute
{
    public DatasetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DatasetWorld.SkipVariable) == "1")
            Skip = $"skipped explicitly: {DatasetWorld.SkipVariable}=1";
    }
}

/// <summary>
/// Issue #38: the control output for a dataset reproduces its expected_findings.csv (every expected row has an exception with the same control,
/// reason code, severity and source reference) and nothing else is produced except what expected_extra_findings.csv allows. The dataset directory is
/// PROOFLY_DATASET_DIR, default the committed examples/synthetic-data/2026-10-27. Analysis of the deviations: docs/testing/dataset-findings-report.md.
/// </summary>
public sealed class DatasetFindingsTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string AllowListFile = Path.Combine(AppContext.BaseDirectory, "Findings", "expected_extra_findings.csv");
    private static readonly string GapsFile = Path.Combine(AppContext.BaseDirectory, "Findings", "known_dataset_gaps.csv");
    private readonly DatasetWorld _world = new();

    public Task InitializeAsync() => _world.InitializeAsync();
    public Task DisposeAsync() => _world.DisposeAsync();

    private static IReadOnlyList<AllowedExtra> AllowList()
    {
        if (Environment.GetEnvironmentVariable(DatasetWorld.AllowListVariable) is { Length: > 0 } custom) return FindingsComparison.ReadAllowed(custom);
        return DatasetWorld.IsCommitted ? FindingsComparison.ReadAllowed(AllowListFile).Where(a => a.Dataset == "committed").ToList() : [];
    }

    private static IReadOnlySet<string> KnownGaps()
    {
        if (Environment.GetEnvironmentVariable(DatasetWorld.GapsVariable) is { Length: > 0 } custom) return FindingsComparison.ReadGaps(custom).Select(g => g.FindingId).ToHashSet();
        return DatasetWorld.IsCommitted ? FindingsComparison.ReadGaps(GapsFile).Where(g => g.Dataset == "committed").Select(g => g.FindingId).ToHashSet() : new HashSet<string>();
    }

    [DatasetFact]
    public async Task Pipeline_reproduces_the_expected_findings_of_the_dataset_and_nothing_else()
    {
        var dir = DatasetWorld.DatasetDirectory;
        output.WriteLine("dataset: " + dir);
        var load = await _world.LoadAsync();
        output.WriteLine($"run {load.Run?.RunId}: {load.Run?.Status} {load.Run?.State}, {load.Run?.ExceptionCount} exception(s)");
        foreach (var d in load.Deliveries)
            output.WriteLine($"  {d.SourceName,-26} {d.DeliveryId} state={d.Ingestion?.State} accepted={d.Ingestion?.AcceptedCount} rejected={d.Ingestion?.RejectedCount} {string.Join(",", (d.Ingestion?.Failures ?? d.Failures).Select(f => f.ReasonCode))}");

        var expected = FindingsComparison.ReadExpected(Path.Combine(dir, "expected_findings.csv"));
        var produced = await _world.ExceptionsAsync();
        var result = FindingsComparison.Compare(expected, produced, AllowList(), KnownGaps());
        foreach (var (e, n) in result.Reproduced) output.WriteLine($"  OK {e.FindingId} {e.Control}/{e.ReasonCode}/{e.Severity} {e.SourceFile} {e.SourceRowId} ({n} exception(s))");
        foreach (var p in result.Allowed) output.WriteLine($"  ALLOWED EXTRA {p}");
        output.WriteLine(result.Report());
        Assert.True(result.IsClean, "The produced exceptions deviate from " + Path.Combine(dir, "expected_findings.csv") + Environment.NewLine + result.Report());
    }

    [DatasetFact]
    public async Task Loading_the_dataset_twice_registers_and_processes_nothing_twice()
    {
        var first = await _world.LoadAsync();
        var exceptions = (await _world.ExceptionsAsync()).Count;
        var deliveries = await ScalarAsync("SELECT count(*) FROM source_deliveries");
        var records = await ScalarAsync("SELECT (SELECT count(*) FROM transfer_instructions) + (SELECT count(*) FROM ledger_entries) + (SELECT count(*) FROM bank_transactions)");
        var audit = await ScalarAsync("SELECT count(*) FROM audit_events");

        var second = await _world.LoadAsync();

        Assert.All(second.Deliveries, d => Assert.False(d.Registered, $"{d.DeliveryId} was registered again"));
        Assert.All(second.Deliveries.Where(d => d.Ingestion is not null), d => Assert.True(d.Ingestion!.AlreadyIngested, $"{d.DeliveryId} was ingested again"));
        Assert.Equal(Orchestration(first), Orchestration(second, expectAlready: true));
        Assert.Equal(exceptions, (await _world.ExceptionsAsync()).Count);
        Assert.Equal(deliveries, await ScalarAsync("SELECT count(*) FROM source_deliveries"));
        Assert.Equal(records, await ScalarAsync("SELECT (SELECT count(*) FROM transfer_instructions) + (SELECT count(*) FROM ledger_entries) + (SELECT count(*) FROM bank_transactions)"));
        Assert.Equal(audit, await ScalarAsync("SELECT count(*) FROM audit_events"));
    }

    private static string? Orchestration(Proofly.Data.Fixtures.DatasetLoadResult r, bool expectAlready = false) =>
        expectAlready ? (r.Run!.Status == Proofly.Data.Orchestration.RunProcessingStatus.AlreadyProcessed ? r.Run.RunId : "reprocessed " + r.Run.RunId) : r.Run?.RunId;

    private async Task<long> ScalarAsync(string sql)
    {
        await using var c = _world.Db.CreateCommand(sql);
        return Convert.ToInt64(await c.ExecuteScalarAsync());
    }
}
