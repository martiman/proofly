using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Proofly.Benchmarks;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Csv;
using Proofly.Data;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

// Dev seed (web app against the real API, #54/#55): dotnet run --project backend/tools/Proofly.Benchmarks -- --seed-dev --scale 0.02 --database proofly_web --evidence-dir .dev/evidence
//   generates the synthetic deliveries, ingests them through the real registry/ingestor and runs run.process once (no reprocess, no API stage,
//   no report). The database is migrated and kept; it is only dropped with --recreate. If it already holds a run it is left alone.
// Usage: dotnet run -c Release --project backend/tools/Proofly.Benchmarks -- --scale 0.1 --out results-0.1.json [--keep] [--skip-api]
var argv = args.ToList();
string Opt(string name, string def) { var i = argv.IndexOf(name); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : def; }
var scale = double.Parse(Opt("--scale", "1.0"), CultureInfo.InvariantCulture);
var outFile = Opt("--out", $"bench-{scale.ToString(CultureInfo.InvariantCulture)}.json");
var seedDev = argv.Contains("--seed-dev");
var db = Opt("--database", Opt("--db", seedDev ? "proofly_web" : "proofly_bench"));
var recreate = argv.Contains("--recreate");
var keep = argv.Contains("--keep") || seedDev;
var skipApi = argv.Contains("--skip-api") || seedDev;
var productionCsv = argv.Contains("--connectors-csv"); // use Proofly.Connectors.Csv instead of the benchmark's own minimal adapters
var apiOnly = argv.Contains("--api-only"); // reuse an existing populated database (from a --keep run) and only run stage 5/6
var distinctRefs = argv.Contains("--distinct-refs");
var apiSeconds = double.Parse(Opt("--api-seconds", "10"), CultureInfo.InvariantCulture);

const string Server = "Host=localhost;Port=55432;Username=proofly";
var cs = $"{Server};Database={db};Maximum Pool Size=40;Pooling=true";
var work = Path.Combine(Path.GetTempPath(), "proofly-bench", scale.ToString(CultureInfo.InvariantCulture));
if (Directory.Exists(work)) Directory.Delete(work, true);
var dataDir = Path.Combine(work, "deliveries");
var evidenceDir = Opt("--evidence-dir", Path.Combine(work, "evidence"));
if (seedDev) evidenceDir = Path.GetFullPath(evidenceDir);

using var metrics = new Metrics();
var report = new Dictionary<string, object?> { ["scale"] = scale, ["machine"] = Environment.MachineName, ["dotnet"] = Environment.Version.ToString(), ["cores"] = Environment.ProcessorCount };

// ---- database: drop and create the dedicated benchmark database, apply all migrations.
if (!apiOnly) await using (var admin = NpgsqlDataSource.Create($"{Server};Database=postgres"))
{
    await using var exists = admin.CreateCommand($"SELECT 1 FROM pg_database WHERE datname = '{db}'");
    var present = await exists.ExecuteScalarAsync() is not null;
    if (!seedDev || recreate)
    {
        await using var c1 = admin.CreateCommand($"DROP DATABASE IF EXISTS {db} WITH (FORCE)"); await c1.ExecuteNonQueryAsync();
        present = false;
    }
    if (!present) { await using var c2 = admin.CreateCommand($"CREATE DATABASE {db}"); await c2.ExecuteNonQueryAsync(); }
}
await using var ds = NpgsqlDataSource.Create(cs);
await new MigrationRunner(ds).RunAsync(); // also applies migrations added after a kept database was created
async Task Exec(string sql) { await using var c = ds.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
async Task<T> Scalar<T>(string sql) { await using var c = ds.CreateCommand(sql); return (T)(await c.ExecuteScalarAsync())!; }
if (seedDev && await Scalar<long>("SELECT count(*) FROM runs") > 0)
{
    Console.WriteLine($"seed-dev: database {db} already holds runs; nothing to do (use --recreate to start over).");
    return 0;
}
if (!apiOnly) await Exec($"INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('LE-BENCH','{(seedDev ? "Proofly Demo Ltd" : "Bench Ltd")}','GBP','{{EUR,JPY}}') ON CONFLICT DO NOTHING; INSERT INTO users (id, name, email) VALUES ('u-bench','Bench','bench@x.test') ON CONFLICT DO NOTHING");
if (seedDev && !apiOnly)
{
    await Proofly.Data.Governance.BaselineRuleset.InstallAsync(ds); // before the run: it records ruleset v1 with these parameters
    Console.WriteLine($"seed-dev: baseline ruleset installed ({Proofly.Data.Governance.BaselineRuleset.Entries.Count} config rows, ruleset v1)");
}
Task<long> DbSize() => Scalar<long>($"SELECT pg_database_size('{db}')");

GenerationSummary summary = null!;
RunProcessingOutcome outcome2 = null!;
var audit = new AuditWriter();
var jobs = new JobStore(ds);
var opts = new IngestionOptions();
if (apiOnly)
{
    var latest = await Scalar<string>("SELECT id FROM runs WHERE legal_entity_id='LE-BENCH' ORDER BY run_version DESC LIMIT 1");
    outcome2 = new RunProcessingOutcome(RunProcessingStatus.AlreadyProcessed, latest, null, null, 0, 0);
}
else
{
// ---- 1. generate
summary = await metrics.MeasureAsync("1 generate", () => Task.FromResult(Generator.Generate(dataDir, scale, distinctRefs)), DbSize,
    "csv files written to a temp dir");
report["generated"] = summary;
Console.WriteLine($"  accounts={summary.Accounts} transactions={summary.Transactions} rows={summary.Files.Sum(f => f.Rows)} bytes={summary.Files.Sum(f => f.Bytes) / 1048576} MB");
foreach (var (k, v) in summary.Seeded.OrderBy(x => x.Key)) Console.WriteLine($"  seeded {k}: {v}");

// ---- 2. ingest through the real registry/ingestor
var adapters = new SourceAdapterRegistry(productionCsv
    ? new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCsvAdapters().BuildServiceProvider().GetServices<ISourceAdapter>().ToList()
    : CsvAdapters.All());
Console.WriteLine($"  csv adapters: {(productionCsv ? "Proofly.Connectors.Csv (production)" : "benchmark's own minimal adapters")}");
var store = new EvidenceStore(new EvidenceOptions { Directory = evidenceDir });
var registry = new DeliveryRegistry(ds, audit, jobs, adapters, opts);
var ingestor = new DeliveryIngestor(ds, audit, store, adapters, opts);
var ingestDetail = new List<object>();
await metrics.MeasureAsync("2 ingest (8 deliveries)", async () =>
{
    foreach (var f in summary.Files)
    {
        var sw = Stopwatch.StartNew();
        EvidenceObject obj;
        await using (var fs = File.OpenRead(f.Path)) obj = await store.WriteAsync(fs);
        var tEvidence = sw.Elapsed.TotalSeconds;
        var manifest = new DeliveryManifestEntry(f.Source, f.DeliveryId, DateOnly.Parse(Generator.Date), "1.0", Path.GetFileName(f.Path), f.Rows,
            f.Totals.OrderBy(t => t.Key).Select(t => new Proofly.Domain.Money(t.Value, t.Key)).ToList(), DateTimeOffset.Parse("2026-10-28T06:00:00+01:00"),
            DateTimeOffset.Parse("2026-10-28T05:42:00+01:00"), null);
        var reg = await registry.RegisterAsync(new DeliveryRegistration(manifest, obj.Sha256, SourceFormat.Csv, Generator.Entity));
        sw.Restart();
        var o = await ingestor.IngestAsync(f.DeliveryId);
        var secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  {f.Source,-26} rows={f.Rows,7} state={o.State,-9} accepted={o.AcceptedCount,7} rejected={o.RejectedCount,5} evidence={tEvidence:F2}s ingest={secs:F2}s ({f.Rows / secs:F0} rows/s) failures={string.Join(",", o.Failures.Select(x => x.ReasonCode))}");
        ingestDetail.Add(new { f.Source, f.Rows, State = o.State.ToString(), o.AcceptedCount, o.RejectedCount, EvidenceSeconds = tEvidence, IngestSeconds = secs, RowsPerSecond = f.Rows / secs, Failures = o.Failures.Select(x => x.ReasonCode) });
    }
    return 0;
}, DbSize, $"{summary.Files.Sum(f => f.Rows)} rows");
report["ingest"] = ingestDetail;

// ---- 3. run.process
var runOptions = new RunProcessingOptions();
var processor = new RunProcessor(ds, audit, opts, runOptions);
var payload = new ProcessRunPayload(Generator.Entity, DateOnly.Parse(Generator.Date));
var outcome = await metrics.MeasureAsync("3 run.process", () => processor.ProcessAsync(payload), DbSize);
Console.WriteLine($"  {outcome.Status} {outcome.RunId} state={outcome.State} exceptions={outcome.ExceptionCount} hash={outcome.ResultHash?[..12]}");
foreach (var (phase, ms) in metrics.Phases("3 run.process")) Console.WriteLine($"    phase {phase}: {ms / 1000:F2}s");
report["process"] = new { outcome, phases = metrics.Phases("3 run.process").ToDictionary(p => p.Phase, p => p.Ms / 1000) };
await Counts("after process");

if (seedDev)
{
    outcome2 = outcome;
    Console.WriteLine($"seed-dev: database {db} ready, run {outcome.RunId} ({outcome.State}), evidence in {evidenceDir}");
    return 0;
}

// ---- 4. reprocess with unchanged evidence
var rowVersion = await Scalar<int>($"SELECT version FROM runs WHERE id='{outcome.RunId}'");
var reprocess = new RunReprocessService(ds, audit, jobs, runOptions);
var req = await reprocess.RequestAsync(new ReprocessRequest(outcome.RunId!, rowVersion, "Benchmark reprocess with unchanged evidence", new AuditActor(ActorType.User, "u-bench", "Bench")));
outcome2 = await metrics.MeasureAsync("4 reprocess (v2)", () => processor.ProcessAsync(new ProcessRunPayload(Generator.Entity, DateOnly.Parse(Generator.Date), req.NewRunId, "reprocess")), DbSize);
Console.WriteLine($"  {outcome2.Status} {outcome2.RunId} state={outcome2.State} exceptions={outcome2.ExceptionCount} carried={outcome2.CarriedOverCount} sameHash={outcome.ResultHash == outcome2.ResultHash}");
foreach (var (phase, ms) in metrics.Phases("4 reprocess (v2)")) Console.WriteLine($"    phase {phase}: {ms / 1000:F2}s");
report["reprocess"] = new { outcome2, sameHash = outcome.ResultHash == outcome2.ResultHash, phases = metrics.Phases("4 reprocess (v2)").ToDictionary(p => p.Phase, p => p.Ms / 1000) };
await Counts("after reprocess");

}

// ---- 5. API reads against the populated database (latest run = v2)
var apiResults = new List<EndpointResult>();
if (!skipApi)
{
    var runId = outcome2.RunId!;
    var accountIds = new List<(string Id, string Ccy)>();
    await using (var c = ds.CreateCommand($"SELECT account_id, currency::text FROM account_control_results WHERE run_id='{runId}' ORDER BY account_id LIMIT 500 OFFSET 1000"))
    await using (var r = await c.ExecuteReaderAsync()) while (await r.ReadAsync()) accountIds.Add((r.GetString(0), r.GetString(1)));
    await metrics.MeasureAsync("5 api reads", async () =>
    {
        await using var api = await ApiBench.StartAsync(cs, evidenceDir);
        var clients = 4;
        // deep page: follow nextCursor 20 pages in
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            using var doc = JsonDocument.Parse(await api.GetAsync($"/api/v1/runs/{runId}/exceptions?limit=50" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            cursor = doc.RootElement.GetProperty("page").TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            if (cursor is null) break;
        }
        var deep = cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor);
        var runs = $"/api/v1/runs/{runId}";
        var plan = new (string Name, Func<int, string> Url, int Max, double Secs)[]
        {
            ("control room", _ => $"{runs}/control-room", 400, apiSeconds),
            ("run detail", _ => runs, 400, apiSeconds),
            ("exceptions page 1 (50)", _ => $"{runs}/exceptions?limit=50", 400, apiSeconds),
            ("exceptions page 21 (cursor)", _ => $"{runs}/exceptions?limit=50{deep}", 400, apiSeconds),
            ("exceptions filter control+severity+status", _ => $"{runs}/exceptions?limit=50&control=matching&severity=MEDIUM&status=OPEN", 400, apiSeconds),
            ("exceptions filter blocking", _ => $"{runs}/exceptions?limit=50&blocking=true", 400, apiSeconds),
            ("exceptions filter currency+value range", _ => $"{runs}/exceptions?limit=50&currency=GBP&valueMin=100&valueMax=100000", 400, apiSeconds),
            ("exceptions search", i => $"{runs}/exceptions?limit=50&search=TRF-00{1000 + i % 500:D4}", 400, apiSeconds),
            ("exceptions summary", _ => $"{runs}/exceptions/summary", 400, apiSeconds),
            ("transactions page 1 (50)", _ => $"{runs}/transactions?limit=50", 100, apiSeconds),
            ("transactions filter kind+match status", _ => $"{runs}/transactions?limit=50&kind=BANK_TRANSFER&matchStatus=UNMATCHED", 100, apiSeconds),
            ("transactions of one account", i => $"{runs}/transactions?limit=50&accountId={accountIds[i % accountIds.Count].Id}", 100, apiSeconds),
            ("accounts page 1 (50)", _ => $"{runs}/accounts?limit=50", 100, apiSeconds),
            ("account ledger", i => $"{runs}/accounts/{accountIds[i % accountIds.Count].Id}/ledger?currency={accountIds[i % accountIds.Count].Ccy}", 200, apiSeconds),
            ("safeguarding position", _ => $"{runs}/safeguarding", 200, apiSeconds),
            ("run sign-off", _ => $"{runs}/sign-off", 200, apiSeconds),
        };
        foreach (var p in plan)
        {
            var res = await api.RunAsync(p.Name, p.Url, clients, p.Max, p.Secs);
            apiResults.Add(res);
            Console.WriteLine($"  {res.Name,-44} n={res.Requests,4} err={res.Errors,3} p50={res.P50,8:F1} p95={res.P95,8:F1} p99={res.P99,8:F1} max={res.MaxMs,8:F1} ms  {res.Rps:F1} req/s  {res.ResponseBytes / 1024} KB");
        }
        return 0;
    }, DbSize, "4 concurrent clients, real Kestrel on loopback");
}
report["api"] = apiResults;

// ---- 6. size, slowest SQL
var sizes = new List<object>();
await using (var c = ds.CreateCommand("SELECT relname, pg_total_relation_size(oid), pg_relation_size(oid), reltuples::bigint FROM pg_class WHERE relkind='r' AND relnamespace='public'::regnamespace ORDER BY 2 DESC LIMIT 15"))
await using (var r = await c.ExecuteReaderAsync()) while (await r.ReadAsync()) sizes.Add(new { Table = r.GetString(0), TotalBytes = r.GetInt64(1), HeapBytes = r.GetInt64(2), EstRows = r.GetInt64(3) });
report["tableSizes"] = sizes;
var slow = metrics.SlowestSql(25).Select(x => new { x.Stage, x.Sql, x.Count, TotalMs = x.TotalMs, MaxMs = x.MaxMs }).ToList();
report["slowestSql"] = slow;
foreach (var s in slow.Take(15)) Console.WriteLine($"  SQL [{s.Stage}] total={s.TotalMs / 1000:F2}s n={s.Count} max={s.MaxMs:F0}ms  {s.Sql}");
report["stages"] = metrics.Results;
report["dbBytes"] = await DbSize();
Console.WriteLine($"  db size: {await DbSize() / 1048576} MB");
File.WriteAllText(outFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"results: {Path.GetFullPath(outFile)}");

if (!keep && !apiOnly)
{
    await ds.DisposeAsync();
    NpgsqlConnection.ClearAllPools();
    await using var admin = NpgsqlDataSource.Create($"{Server};Database=postgres");
    await using var c = admin.CreateCommand($"DROP DATABASE IF EXISTS {db} WITH (FORCE)"); await c.ExecuteNonQueryAsync();
}
if (Directory.Exists(work)) Directory.Delete(work, true);
return 0;

async Task Counts(string label)
{
    var line = new List<string>();
    foreach (var t in new[] { "exceptions", "exception_links", "exception_rule_traces", "match_groups", "match_candidates", "account_control_results", "audit_events", "exception_timeline" })
        line.Add($"{t}={await Scalar<long>($"SELECT count(*) FROM {t}")}");
    Console.WriteLine($"  {label}: " + string.Join(" ", line));
}
