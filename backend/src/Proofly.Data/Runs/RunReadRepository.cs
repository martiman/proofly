using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Proofly.Data.Runs;

public interface IRunReadRepository
{
    Task<IReadOnlyList<LegalEntityRow>> ListLegalEntitiesAsync(EntityScope scope, CancellationToken ct = default);
    Task<RunPageRows> ListRunsAsync(EntityScope scope, RunListFilter filter, int limit, RunCursor? after, bool includeTotal, CancellationToken ct = default);
    /// <summary>Null when the run does not exist or its legal entity is outside the scope (indistinguishable on purpose).</summary>
    Task<RunRow?> GetRunAsync(EntityScope scope, string runId, CancellationToken ct = default);
    Task<ControlRoomRows?> GetControlRoomAsync(EntityScope scope, string runId, CancellationToken ct = default);
}

/// <summary>Read-only queries for legal entities, runs and the control room. Every query is scoped to the caller's legal entities; all aggregates are computed in SQL over NUMERIC.</summary>
public sealed class RunReadRepository(NpgsqlDataSource dataSource, TimeProvider clock) : IRunReadRepository
{
    public const int AttentionQueueSize = 10;
    /// <summary>Ids listed per delivery and per safeguarding currency in the control room; the count carries the rest (the exception list is the full view).</summary>
    public const int IdsPerItem = 20;
    private const string OpenStatus = "status NOT IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')";
    private static readonly (string Area, string Label)[] Areas =
    [
        ("data_completeness", "Data completeness"), ("matching", "Matching"), ("independent_ledger", "Independent ledger"),
        ("transaction_lifecycle", "Transaction lifecycle"), ("safeguarding", "Safeguarding"),
    ];

    private const string RunSelect = """
        SELECT r.id, r.legal_entity_id, r.business_date, r.run_version, r.ruleset_version, r.state, r.sign_off_state,
               r.started_at, r.last_evidence_arrival_at, r.version,
               (SELECT s.id FROM runs s WHERE s.supersedes_run_id = r.id ORDER BY s.run_version LIMIT 1),
               r.supersedes_run_id, so.prepared_by_user_id, pu.name, so.signed_by_user_id, su.name, so.signed_at, so.required_approver_user_id, au.name
        FROM runs r
        LEFT JOIN LATERAL (SELECT x.* FROM run_signoffs x WHERE x.run_id = r.id ORDER BY x.created_at DESC, x.id DESC LIMIT 1) so ON true
        LEFT JOIN users pu ON pu.id = so.prepared_by_user_id
        LEFT JOIN users su ON su.id = so.signed_by_user_id
        LEFT JOIN users au ON au.id = so.required_approver_user_id
        """;

    private static void Scope(NpgsqlCommand cmd, EntityScope s)
    {
        cmd.Parameters.AddWithValue("all", s.All);
        cmd.Parameters.AddWithValue("ids", s.Ids.ToArray());
    }

    private const string ScopeSql = "(@all OR r.legal_entity_id = ANY(@ids))";

    public async Task<IReadOnlyList<LegalEntityRow>> ListLegalEntitiesAsync(EntityScope scope, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT le.id, le.name, ARRAY(SELECT c FROM unnest(le.currencies) AS c ORDER BY c)
            FROM legal_entities le WHERE (@all OR le.id = ANY(@ids)) ORDER BY le.id
            """);
        Scope(cmd, scope);
        var list = new List<LegalEntityRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { list.Add(new LegalEntityRow(r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2))); }
        return list;
    }

    public async Task<RunPageRows> ListRunsAsync(EntityScope scope, RunListFilter filter, int limit, RunCursor? after, bool includeTotal, CancellationToken ct = default)
    {
        const string where = $"""

            WHERE {ScopeSql}
              AND (@le::text IS NULL OR r.legal_entity_id = @le)
              AND (@bd::date IS NULL OR r.business_date = @bd)
              AND (@sup OR r.state <> 'SUPERSEDED')
            """;
        void Bind(NpgsqlCommand c)
        {
            Scope(c, scope);
            c.Parameters.AddWithValue("le", (object?)filter.LegalEntityId ?? DBNull.Value);
            c.Parameters.Add(new NpgsqlParameter("bd", NpgsqlTypes.NpgsqlDbType.Date) { Value = filter.BusinessDate is { } d ? d : DBNull.Value });
            c.Parameters.AddWithValue("sup", filter.IncludeSuperseded);
        }

        await using var cmd = dataSource.CreateCommand(RunSelect + where + """

              AND (@hasCur = false OR r.business_date < @cd
                   OR (r.business_date = @cd AND (r.legal_entity_id > @cle
                        OR (r.legal_entity_id = @cle AND (r.run_version < @cv OR (r.run_version = @cv AND r.id > @cid))))))
            ORDER BY r.business_date DESC, r.legal_entity_id ASC, r.run_version DESC, r.id ASC
            LIMIT @lim
            """);
        Bind(cmd);
        cmd.Parameters.AddWithValue("hasCur", after is not null);
        cmd.Parameters.Add(new NpgsqlParameter("cd", NpgsqlTypes.NpgsqlDbType.Date) { Value = after?.BusinessDate ?? DateOnly.MinValue });
        cmd.Parameters.AddWithValue("cle", after?.LegalEntityId ?? "");
        cmd.Parameters.AddWithValue("cv", after?.Version ?? 0);
        cmd.Parameters.AddWithValue("cid", after?.Id ?? "");
        cmd.Parameters.AddWithValue("lim", limit + 1);
        var items = await ReadRunsAsync(cmd, ct);

        int? total = null;
        if (includeTotal)
        {
            await using var cnt = dataSource.CreateCommand("SELECT count(*) FROM runs r " + where);
            Bind(cnt);
            total = (int)(long)(await cnt.ExecuteScalarAsync(ct))!;
        }
        return new RunPageRows(items, total);
    }

    public async Task<RunRow?> GetRunAsync(EntityScope scope, string runId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(RunSelect + $" WHERE r.id = @id AND {ScopeSql}");
        cmd.Parameters.AddWithValue("id", runId);
        Scope(cmd, scope);
        return (await ReadRunsAsync(cmd, ct)).SingleOrDefault();
    }

    private static async Task<IReadOnlyList<RunRow>> ReadRunsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<RunRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new RunRow(r.GetString(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : new UserRefRow(r.GetString(12), r.GetString(13)),
                r.IsDBNull(17) ? null : new UserRefRow(r.GetString(17), r.GetString(18)), // named at submission; null while the policy only names the role
                r.IsDBNull(14) ? null : new UserRefRow(r.GetString(14), r.GetString(15)),
                r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16)));
        }
        return list;
    }

    public async Task<ControlRoomRows?> GetControlRoomAsync(EntityScope scope, string runId, CancellationToken ct = default)
    {
        var run = await GetRunAsync(scope, runId, ct);
        if (run is null) { return null; }

        var previousId = await PreviousRunIdAsync(run, ct);
        var deliveries = await DeliveriesAsync(run.Id, ct);
        var areas = await AreasAsync(run.Id, previousId, ct);
        var attention = await AttentionAsync(run.Id, ct);
        var safeguarding = await SafeguardingAsync(run, previousId, ct);
        var summary = await SummaryAsync(run, previousId, deliveries, safeguarding, ct);
        return new ControlRoomRows(run, summary, deliveries, areas, attention, safeguarding);
    }

    private async Task<string?> PreviousRunIdAsync(RunRow run, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id FROM runs WHERE legal_entity_id = @le AND business_date < @bd AND state <> 'SUPERSEDED'
            ORDER BY business_date DESC, run_version DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("le", run.LegalEntityId);
        cmd.Parameters.Add(new NpgsqlParameter("bd", NpgsqlTypes.NpgsqlDbType.Date) { Value = run.BusinessDate });
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task<IReadOnlyList<DeliveryRow>> DeliveriesAsync(string runId, CancellationToken ct)
    {
        var totals = new Dictionary<string, List<MoneyRow>>();
        await using (var t = dataSource.CreateCommand("""
            SELECT t.delivery_id, t.currency::text, t.declared_amount FROM delivery_control_totals t
            JOIN source_deliveries d ON d.id = t.delivery_id WHERE d.run_id = @r AND t.declared_amount IS NOT NULL ORDER BY t.delivery_id, t.currency
            """))
        {
            t.Parameters.AddWithValue("r", runId);
            await using var tr = await t.ExecuteReaderAsync(ct);
            while (await tr.ReadAsync(ct)) { (totals.TryGetValue(tr.GetString(0), out var l) ? l : totals[tr.GetString(0)] = []).Add(new MoneyRow(tr.GetDecimal(2), tr.GetString(1))); }
        }
        var findings = new Dictionary<string, List<string>>();
        var findingCounts = new Dictionary<string, int>();
        await using (var f = dataSource.CreateCommand("SELECT delivery_id, count(*) FROM exceptions WHERE run_id = @r AND delivery_id IS NOT NULL GROUP BY delivery_id"))
        {
            f.Parameters.AddWithValue("r", runId);
            await using var fr = await f.ExecuteReaderAsync(ct);
            while (await fr.ReadAsync(ct)) { findingCounts[fr.GetString(0)] = (int)fr.GetInt64(1); }
        }
        await using (var f = dataSource.CreateCommand("""
            SELECT d.id, x.id FROM source_deliveries d
            CROSS JOIN LATERAL (SELECT e.id FROM exceptions e WHERE e.run_id = @r AND e.delivery_id = d.id ORDER BY e.id LIMIT @lim) x
            WHERE d.run_id = @r ORDER BY d.id, x.id
            """))
        {
            f.Parameters.AddWithValue("r", runId);
            f.Parameters.AddWithValue("lim", IdsPerItem);
            await using var fr = await f.ExecuteReaderAsync(ct);
            while (await fr.ReadAsync(ct)) { (findings.TryGetValue(fr.GetString(0), out var l) ? l : findings[fr.GetString(0)] = []).Add(fr.GetString(1)); }
        }

        await using var cmd = dataSource.CreateCommand("""
            SELECT d.id, d.source_name, d.file_name, d.business_date, d.schema_version, d.declared_record_count,
                   COALESCE(d.total_row_count, d.accepted_record_count + d.rejected_record_count),
                   d.expected_by, d.received_at, d.supersedes_delivery_id,
                   (SELECT s.id FROM source_deliveries s WHERE s.supersedes_delivery_id = d.id ORDER BY s.id LIMIT 1),
                   d.state, d.accepted_record_count, d.rejected_record_count
            FROM source_deliveries d WHERE d.run_id = @r ORDER BY d.source_name, d.business_date, d.id
            """);
        cmd.Parameters.AddWithValue("r", runId);
        var list = new List<DeliveryRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetString(0);
            list.Add(new DeliveryRow(id, r.GetString(1), r.GetString(2), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt32(6),
                totals.GetValueOrDefault(id) ?? [], r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetInt32(12), r.GetInt32(13),
                findings.GetValueOrDefault(id) ?? [], findingCounts.GetValueOrDefault(id)));
        }
        return list;
    }

    /// <summary>Records per control area: delivery rows (completeness), match groups, ledger entries, lifecycle events, safeguarding positions.</summary>
    private async Task<int[]> RecordCountsAsync(string runId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH d AS (SELECT id FROM source_deliveries WHERE run_id = @r)
            SELECT (SELECT COALESCE(sum(COALESCE(total_row_count, accepted_record_count + rejected_record_count)), 0) FROM source_deliveries WHERE run_id = @r),
                   (SELECT count(*) FROM match_groups WHERE run_id = @r),
                   (SELECT count(*) FROM ledger_entries WHERE delivery_id IN (SELECT id FROM d)),
                   (SELECT count(*) FROM transfer_instructions WHERE delivery_id IN (SELECT id FROM d))
                     + (SELECT count(*) FROM direct_debit_events WHERE delivery_id IN (SELECT id FROM d))
                     + (SELECT count(*) FROM card_events WHERE delivery_id IN (SELECT id FROM d)),
                   (SELECT count(*) FROM safeguarding_positions WHERE run_id = @r)
            """);
        cmd.Parameters.AddWithValue("r", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Enumerable.Range(0, 5).Select(i => (int)r.GetInt64(i)).ToArray();
    }

    private async Task<IReadOnlyList<ControlAreaRow>> AreasAsync(string runId, string? previousId, CancellationToken ct)
    {
        var counts = await RecordCountsAsync(runId, ct);
        var previous = previousId is null ? null : await RecordCountsAsync(previousId, ct);

        var ex = new Dictionary<string, (int Total, int Open, int Blocking)>();
        await using (var c = dataSource.CreateCommand($"""
            SELECT control, count(*), count(*) FILTER (WHERE {OpenStatus}), count(*) FILTER (WHERE {OpenStatus} AND blocking)
            FROM exceptions WHERE run_id = @r GROUP BY control
            """))
        {
            c.Parameters.AddWithValue("r", runId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { ex[r.GetString(0)] = ((int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3)); }
        }
        var values = new Dictionary<string, List<MoneyRow>>();
        await using (var c = dataSource.CreateCommand($"""
            SELECT control, value_currency::text, sum(value_amount) FROM exceptions
            WHERE run_id = @r AND {OpenStatus} AND value_amount IS NOT NULL GROUP BY control, value_currency ORDER BY control, value_currency
            """))
        {
            c.Parameters.AddWithValue("r", runId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { (values.TryGetValue(r.GetString(0), out var l) ? l : values[r.GetString(0)] = []).Add(new MoneyRow(r.GetDecimal(2), r.GetString(1))); }
        }
        var qualified = await ScalarAsync("SELECT count(*) FROM source_deliveries WHERE run_id = @r AND state = 'QUALIFIED'", runId, ct);
        var sgQualified = await ScalarAsync("SELECT count(*) FROM safeguarding_positions WHERE run_id = @r AND state = 'QUALIFIED'", runId, ct);
        var sgShortfall = await ScalarAsync("SELECT count(*) FROM safeguarding_positions WHERE run_id = @r AND state = 'SHORTFALL'", runId, ct);

        // An area with neither records nor exceptions has nothing to report; it is omitted rather than shown as PASSED.
        var list = new List<ControlAreaRow>();
        for (var i = 0; i < Areas.Length; i++)
        {
            var (area, label) = Areas[i];
            var e = ex.GetValueOrDefault(area);
            if (counts[i] == 0 && e.Total == 0) { continue; }
            var state = e.Blocking > 0 ? "BLOCKED"
                : area == "safeguarding" && sgShortfall > 0 ? "FAILED"
                : e.Open > 0 ? "REVIEW_REQUIRED"
                : (area == "data_completeness" && qualified > 0) || (area == "safeguarding" && sgQualified > 0) ? "QUALIFIED"
                : "PASSED";
            var prev = previous?[i] ?? 0;
            list.Add(new ControlAreaRow(area, label, state, counts[i], values.GetValueOrDefault(area) ?? [], e.Open, prev, previous is null ? 0 : counts[i] - prev));
        }
        return list;
    }

    private async Task<int> ScalarAsync(string sql, string runId, CancellationToken ct)
    {
        await using var c = dataSource.CreateCommand(sql);
        c.Parameters.AddWithValue("r", runId);
        return (int)(long)(await c.ExecuteScalarAsync(ct))!;
    }

    private async Task<IReadOnlyList<AttentionRow>> AttentionAsync(string runId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT e.id, e.run_id, e.title, e.control, e.reason_code, e.severity, e.status, e.value_amount, e.value_currency::text, e.legal_entity_id,
                   e.business_date, e.owner_user_id, u.name, e.created_at, e.due_at, e.blocking, e.source_file, e.source_row_id, e.version
            FROM exceptions e LEFT JOIN users u ON u.id = e.owner_user_id
            WHERE e.run_id = @r AND e.{OpenStatus}
            ORDER BY e.blocking DESC,
                     CASE e.severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END,
                     e.due_at, e.id
            LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("r", runId);
        cmd.Parameters.AddWithValue("lim", AttentionQueueSize);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var list = new List<AttentionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var created = r.GetFieldValue<DateTimeOffset>(13);
            list.Add(new AttentionRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : new MoneyRow(r.GetDecimal(7), r.GetString(8)), r.GetString(9), DateOnly.FromDateTime(r.GetDateTime(10)),
                r.IsDBNull(11) ? null : new UserRefRow(r.GetString(11), r.GetString(12)), created,
                Math.Max(0, today.DayNumber - DateOnly.FromDateTime(created.UtcDateTime).DayNumber), r.GetFieldValue<DateTimeOffset>(14), r.GetBoolean(15),
                r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17), r.GetInt32(18)));
        }
        return list;
    }

    private async Task<IReadOnlyList<SafeguardingRow>> SafeguardingAsync(RunRow run, string? previousId, CancellationToken ct)
    {
        var adjustments = new Dictionary<string, decimal>();
        await using (var c = dataSource.CreateCommand("""
            SELECT currency::text, sum(amount) FROM safeguarding_adjustments
            WHERE run_id = @r AND status = 'APPROVED' AND (expires_on IS NULL OR expires_on >= @bd) GROUP BY currency
            """))
        {
            c.Parameters.AddWithValue("r", run.Id);
            c.Parameters.Add(new NpgsqlParameter("bd", NpgsqlTypes.NpgsqlDbType.Date) { Value = run.BusinessDate });
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { adjustments[r.GetString(0)] = r.GetDecimal(1); }
        }
        var previous = new Dictionary<string, decimal>();
        if (previousId is not null)
        {
            await using var c = dataSource.CreateCommand("SELECT currency::text, difference_amount FROM safeguarding_positions WHERE run_id = @r AND difference_amount IS NOT NULL");
            c.Parameters.AddWithValue("r", previousId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { previous[r.GetString(0)] = r.GetDecimal(1); }
        }
        var exceptionIds = new Dictionary<string, List<string>>();
        var exceptionCounts = new Dictionary<string, int>();
        await using (var c = dataSource.CreateCommand($"""
            SELECT ccy, id, cnt FROM (
              SELECT value_currency::text AS ccy, id, row_number() OVER (PARTITION BY value_currency ORDER BY id) AS rn, count(*) OVER (PARTITION BY value_currency) AS cnt
              FROM exceptions WHERE run_id = @r AND control = 'safeguarding' AND {OpenStatus} AND value_currency IS NOT NULL) s
            WHERE rn <= @lim ORDER BY ccy, id
            """))
        {
            c.Parameters.AddWithValue("r", run.Id);
            c.Parameters.AddWithValue("lim", (long)IdsPerItem);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                (exceptionIds.TryGetValue(r.GetString(0), out var l) ? l : exceptionIds[r.GetString(0)] = []).Add(r.GetString(1));
                exceptionCounts[r.GetString(0)] = (int)r.GetInt64(2);
            }
        }

        await using var cmd = dataSource.CreateCommand("""
            SELECT currency::text, obligation_amount, eligible_amount, difference_amount, state, detail::text, stale
            FROM safeguarding_positions WHERE run_id = @r ORDER BY currency
            """);
        cmd.Parameters.AddWithValue("r", run.Id);
        var list = new List<SafeguardingRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var ccy = rd.GetString(0);
            list.Add(new SafeguardingRow(ccy, new MoneyRow(rd.GetDecimal(1), ccy), new MoneyRow(rd.GetDecimal(2), ccy),
                new MoneyRow(adjustments.GetValueOrDefault(ccy), ccy), rd.IsDBNull(3) ? null : new MoneyRow(rd.GetDecimal(3), ccy), rd.GetString(4), Qualifications(rd.GetString(5)),
                previous.TryGetValue(ccy, out var p) ? new MoneyRow(p, ccy) : null, exceptionIds.GetValueOrDefault(ccy) ?? [], rd.GetBoolean(6), exceptionCounts.GetValueOrDefault(ccy)));
        }
        return list;
    }

    private static IReadOnlyList<string> Qualifications(string detailJson)
    {
        using var doc = JsonDocument.Parse(detailJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("qualifications", out var q) && q.ValueKind == JsonValueKind.Array
            ? q.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }

    private async Task<IReadOnlyList<DailySummaryRow>> SummaryAsync(RunRow run, string? previousId, IReadOnlyList<DeliveryRow> deliveries,
        IReadOnlyList<SafeguardingRow> safeguarding, CancellationToken ct)
    {
        var items = new List<DailySummaryRow>();
        int open, blocking, total;
        await using (var c = dataSource.CreateCommand($"""
            SELECT count(*) FILTER (WHERE {OpenStatus}), count(*) FILTER (WHERE {OpenStatus} AND blocking), count(*) FROM exceptions WHERE run_id = @r
            """))
        {
            c.Parameters.AddWithValue("r", run.Id);
            await using var r = await c.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            open = (int)r.GetInt64(0); blocking = (int)r.GetInt64(1); total = (int)r.GetInt64(2);
        }
        if (open > 0) { items.Add(new DailySummaryRow($"{open} open exception(s), {blocking} blocking.", null, null)); }

        if (previousId is not null)
        {
            var previousTotal = await ScalarAsync("SELECT count(*) FROM exceptions WHERE run_id = @r", previousId, ct);
            items.Add(new DailySummaryRow($"Exceptions raised: {total} ({previousTotal} for the previous business date, run {previousId}).", null, null));
        }
        foreach (var d in deliveries.Where(d => d.State != "RECEIVED"))
        {
            items.Add(new DailySummaryRow($"Source {d.SourceName} is {d.State}.", "source", d.Id));
        }
        foreach (var s in safeguarding.Where(s => s.State == "SHORTFALL" && s.Result is not null))
        {
            items.Add(new DailySummaryRow($"Safeguarding shortfall in {s.Currency}: {s.Result!.Amount.ToString("0.00####", CultureInfo.InvariantCulture)}.", "control", "safeguarding"));
        }
        return items;
    }
}
