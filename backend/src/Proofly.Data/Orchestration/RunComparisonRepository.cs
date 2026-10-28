using System.Text.Json;
using Npgsql;
using Proofly.Data.Runs;

namespace Proofly.Data.Orchestration;

public sealed record ComparisonAreaRow(string Area, string State, string AgainstState, int RecordCountDelta);

/// <param name="Label">What changed (client obligation, eligible balances, approved adjustments, position).</param>
public sealed record ComparisonDriverRow(string Label, MoneyRow Change);

/// <summary>
/// Differences of <c>run</c> compared with <c>against</c>. An exception is identified across versions by its finding key.
/// <see cref="NewExceptionIds"/> belong to the run, <see cref="ResolvedExceptionIds"/> to the compared run (the finding no longer
/// occurs). <see cref="ChangedExceptionIds"/> (same finding, different severity, blocking, reason or value) are not part of the
/// published contract yet.
/// </summary>
public sealed record RunComparisonRows(
    IReadOnlyList<ComparisonAreaRow> Areas, IReadOnlyList<string> NewExceptionIds, IReadOnlyList<string> ResolvedExceptionIds,
    IReadOnlyList<string> ChangedExceptionIds, IReadOnlyList<ComparisonDriverRow> SafeguardingDelta, IReadOnlyList<string> ChangedDeliveries);

/// <summary>Read side of compareRuns. Scope and permission checks are done by the caller; both runs must exist.</summary>
public sealed class RunComparisonRepository(NpgsqlDataSource dataSource)
{
    private static readonly string[] AreaOrder = ["data_completeness", "matching", "independent_ledger", "transaction_lifecycle", "safeguarding"];

    public async Task<RunComparisonRows> CompareAsync(string runId, string againstId, CancellationToken ct = default)
    {
        var areas = new Dictionary<string, (string State, int Count)>[2] { [], [] };
        for (var i = 0; i < 2; i++)
        {
            await using var c = dataSource.CreateCommand("SELECT control, state, record_count FROM run_control_results WHERE run_id=@r");
            c.Parameters.AddWithValue("r", i == 0 ? runId : againstId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) areas[i][r.GetString(0)] = (r.GetString(1), r.GetInt32(2));
        }
        var areaRows = AreaOrder.Where(a => areas[0].ContainsKey(a) && areas[1].ContainsKey(a))
            .Select(a => new ComparisonAreaRow(a, areas[0][a].State, areas[1][a].State, areas[0][a].Count - areas[1][a].Count)).ToList();

        var added = await Ids("""
            SELECT e.id FROM exceptions e WHERE e.run_id=@a
              AND NOT EXISTS (SELECT 1 FROM exceptions o WHERE o.run_id=@b AND e.finding_key IS NOT NULL AND o.finding_key = e.finding_key)
            ORDER BY e.id
            """, runId, againstId, ct);
        var resolved = await Ids("""
            SELECT e.id FROM exceptions e WHERE e.run_id=@a
              AND NOT EXISTS (SELECT 1 FROM exceptions o WHERE o.run_id=@b AND e.finding_key IS NOT NULL AND o.finding_key = e.finding_key)
            ORDER BY e.id
            """, againstId, runId, ct);
        var changed = await Ids("""
            SELECT e.id FROM exceptions e JOIN exceptions o ON o.run_id=@b AND o.finding_key = e.finding_key
            WHERE e.run_id=@a AND (e.severity <> o.severity OR e.blocking <> o.blocking OR e.reason_code <> o.reason_code
                                   OR e.value_amount IS DISTINCT FROM o.value_amount OR e.value_currency IS DISTINCT FROM o.value_currency)
            ORDER BY e.id
            """, runId, againstId, ct);

        var positions = new Dictionary<string, (decimal Obligation, decimal Eligible, decimal Result)>[2] { [], [] };
        for (var i = 0; i < 2; i++)
        {
            await using var c = dataSource.CreateCommand("SELECT currency::text, obligation_amount, eligible_amount, difference_amount FROM safeguarding_positions WHERE run_id=@r");
            c.Parameters.AddWithValue("r", i == 0 ? runId : againstId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) positions[i][r.GetString(0)] = (r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3));
        }
        var drivers = new List<ComparisonDriverRow>();
        foreach (var ccy in positions[0].Keys.Union(positions[1].Keys).OrderBy(c => c, StringComparer.Ordinal))
        {
            var a = positions[0].GetValueOrDefault(ccy);
            var b = positions[1].GetValueOrDefault(ccy);
            void Add(string label, decimal delta) { if (delta != 0m) drivers.Add(new ComparisonDriverRow(label, new MoneyRow(delta, ccy))); }
            Add("Client obligation", a.Obligation - b.Obligation);
            Add("Eligible balances", a.Eligible - b.Eligible);
            Add("Safeguarding position", a.Result - b.Result);
        }

        var manifests = new List<Dictionary<string, string>>();
        for (var i = 0; i < 2; i++)
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var c = dataSource.CreateCommand("SELECT input_manifest::text FROM runs WHERE id=@r");
            c.Parameters.AddWithValue("r", i == 0 ? runId : againstId);
            if (await c.ExecuteScalarAsync(ct) is string json)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var d in doc.RootElement.EnumerateArray()) m[d.GetProperty("deliveryId").GetString()!] = d.GetProperty("fingerprint").GetString()!;
            }
            manifests.Add(m);
        }
        var deliveries = manifests[0].Keys.Union(manifests[1].Keys).Where(k => manifests[0].GetValueOrDefault(k) != manifests[1].GetValueOrDefault(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        return new RunComparisonRows(areaRows, added, resolved, changed, drivers, deliveries);
    }

    private async Task<IReadOnlyList<string>> Ids(string sql, string a, string b, CancellationToken ct)
    {
        await using var c = dataSource.CreateCommand(sql);
        c.Parameters.AddWithValue("a", a);
        c.Parameters.AddWithValue("b", b);
        var list = new List<string>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }
}
