using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Proofly.Benchmarks;

public sealed record StageResult(string Stage, double Seconds, double PeakWorkingSetMb, double PeakManagedMb, long DbBytes, string Note);

/// <summary>
/// Wall clock, peak working set of THIS process (sampled every 25 ms; includes the generator, the API host and the benchmark itself, not
/// the PostgreSQL server processes) and peak managed heap per stage. Every Npgsql command is timed through the driver's ActivitySource, so
/// the slowest statements can be listed without pg_stat_statements (which is not preloaded on the dev server).
/// </summary>
public sealed class Metrics : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentDictionary<(string Stage, string Sql), (long Count, double TotalMs, double MaxMs)> _sql = new();
    private readonly ConcurrentDictionary<(string Stage, string Phase), double> _phases = new();
    public string CurrentStage = "setup";
    public readonly List<StageResult> Results = [];

    public Metrics()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Npgsql" or "Proofly.Runs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.Source.Name == "Proofly.Runs") { _phases.AddOrUpdate((CurrentStage, a.OperationName), a.Duration.TotalMilliseconds, (_, v) => v + a.Duration.TotalMilliseconds); return; }
                var text = a.GetTagItem("db.query.text") as string ?? a.GetTagItem("db.statement") as string ?? a.DisplayName;
                text = Regex.Replace(text ?? "?", @"\s+", " ").Trim();
                if (text.Length > 140) text = text[..140];
                var ms = a.Duration.TotalMilliseconds;
                _sql.AddOrUpdate((CurrentStage, text), (1, ms, ms), (_, v) => (v.Count + 1, v.TotalMs + ms, Math.Max(v.MaxMs, ms)));
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> body, Func<Task<long>> dbSize, string note = "")
    {
        CurrentStage = stage;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        double peakWs = 0, peakHeap = 0;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                proc.Refresh();
                peakWs = Math.Max(peakWs, proc.WorkingSet64 / 1048576.0);
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false) / 1048576.0);
                try { await Task.Delay(25, cts.Token); } catch (OperationCanceledException) { }
            }
        });
        var sw = Stopwatch.StartNew();
        T result;
        try { result = await body(); }
        finally { sw.Stop(); cts.Cancel(); await sampler; }
        Results.Add(new StageResult(stage, sw.Elapsed.TotalSeconds, peakWs, peakHeap, await dbSize(), note));
        Console.WriteLine($"[{stage}] {sw.Elapsed.TotalSeconds:F1}s  peakWS={peakWs:F0}MB  peakHeap={peakHeap:F0}MB  {note}");
        return result;
    }

    public IEnumerable<(string Phase, double Ms)> Phases(string stage) => _phases.Where(p => p.Key.Stage == stage).Select(p => (p.Key.Phase, p.Value)).OrderBy(p => p.Phase);

    public IEnumerable<(string Stage, string Sql, long Count, double TotalMs, double MaxMs)> SlowestSql(int top) =>
        _sql.Select(kv => (kv.Key.Stage, kv.Key.Sql, kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs)).OrderByDescending(x => x.TotalMs).Take(top);

    public IEnumerable<(string Sql, long Count, double TotalMs, double MaxMs)> SqlForStage(string stage, int top) =>
        _sql.Where(kv => kv.Key.Stage == stage).Select(kv => (kv.Key.Sql, kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs)).OrderByDescending(x => x.TotalMs).Take(top);

    public void Dispose() => _listener.Dispose();
}
