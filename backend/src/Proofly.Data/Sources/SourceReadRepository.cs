using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Sources;

public interface ISourceReadRepository
{
    /// <summary>Deliveries of a run; null when the run does not exist or is outside the scope.</summary>
    Task<IReadOnlyList<DeliverySummary>?> ListRunDeliveriesAsync(string runId, EntityScope scope, CancellationToken ct = default);
    Task<DeliveryDetail?> GetDeliveryAsync(string deliveryId, EntityScope scope, CancellationToken ct = default);
    /// <summary>Records (accepted first in file order, then rejected rows); null when the delivery is not visible.</summary>
    Task<RecordPage?> ListRecordsAsync(string deliveryId, RecordQuery query, EntityScope scope, CancellationToken ct = default);
    /// <summary>The record and its delivery; null when unknown, malformed id or out of scope.</summary>
    Task<(DeliverySummary Delivery, SourceRecordRow Record)?> GetRecordAsync(string recordId, EntityScope scope, CancellationToken ct = default);
    Task<IReadOnlyList<ProcessingStepRow>?> GetProcessingHistoryAsync(string deliveryId, EntityScope scope, CancellationToken ct = default);
}

public sealed class SourceReadRepository(NpgsqlDataSource db) : ISourceReadRepository
{
    private const string RejectedPrefix = "rejected";

    /// <summary>Source name -> record table and the SQL expression (alias t) for the source row id. Add a line per new table.</summary>
    private static readonly Dictionary<string, (string Table, string RowId)> Tables = new(StringComparer.Ordinal)
    {
        ["transfer_instructions"] = ("transfer_instructions", "t.source_row_id"),
        ["direct_debit_events"] = ("direct_debit_events", "t.source_row_id"),
        ["internal_ledger_entries"] = ("ledger_entries", "t.ledger_entry_id"),
        ["client_account_balances"] = ("account_balances", "t.account_id || '/' || t.balance_type || '/' || t.balance_date::text"),
        ["banking_transactions"] = ("bank_transactions", "t.external_transaction_id"),
        ["safeguarding_balances"] = ("safeguarding_balances", "t.source_row_id"),
        ["card_transactions"] = ("card_events", "t.source_row_id"),
        ["card_settlements"] = ("card_settlements", "t.source_row_id"),
    };

    private const string DeliverySelect = """
        SELECT d.id, d.run_id, COALESCE(d.legal_entity_id, r.legal_entity_id), d.source_name, d.business_date, d.schema_version, d.file_name,
               d.fingerprint_sha256, d.declared_record_count, d.expected_by, d.received_at, d.state, d.accepted_record_count,
               d.rejected_record_count, d.total_row_count, d.supersedes_delivery_id,
               (SELECT s.id FROM source_deliveries s WHERE s.supersedes_delivery_id = d.id AND s.state <> 'REJECTED' ORDER BY s.created_at, s.id LIMIT 1),
               d.sequence_number, d.ingested_at IS NOT NULL, d.version,
               COALESCE((SELECT array_agg(e.id ORDER BY e.id) FROM exceptions e WHERE e.delivery_id = d.id), '{}'::text[])
        FROM source_deliveries d LEFT JOIN runs r ON r.id = d.run_id
        """;

    private const string ScopeSql = "(@all OR COALESCE(d.legal_entity_id, r.legal_entity_id) = ANY(@les))";

    private static void AddScope(NpgsqlCommand c, EntityScope s)
    {
        c.Parameters.Add(new NpgsqlParameter("all", NpgsqlDbType.Boolean) { Value = s.All });
        c.Parameters.Add(new NpgsqlParameter("les", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = s.Ids.ToArray() });
    }

    public async Task<IReadOnlyList<DeliverySummary>?> ListRunDeliveriesAsync(string runId, EntityScope scope, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var chk = new NpgsqlCommand("SELECT 1 FROM runs d WHERE d.id = @r AND (@all OR d.legal_entity_id = ANY(@les))", conn))
        {
            chk.Parameters.AddWithValue("r", runId);
            AddScope(chk, scope);
            if (await chk.ExecuteScalarAsync(ct) is null) { return null; }
        }
        await using var c = new NpgsqlCommand($"{DeliverySelect} WHERE d.run_id = @r AND {ScopeSql} ORDER BY d.source_name, d.business_date, d.id", conn);
        c.Parameters.AddWithValue("r", runId);
        AddScope(c, scope);
        return await ReadDeliveriesAsync(conn, c, ct);
    }

    public async Task<DeliveryDetail?> GetDeliveryAsync(string deliveryId, EntityScope scope, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var d = await LoadDeliveryAsync(conn, deliveryId, scope, ct);
        if (d is null) { return null; }
        var failures = new List<DeliveryFailureRow>();
        await using var f = new NpgsqlCommand("SELECT reason_code, reason FROM delivery_rejections WHERE delivery_id = @d AND row_number IS NULL ORDER BY id", conn);
        f.Parameters.AddWithValue("d", deliveryId);
        await using (var rd = await f.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct)) { failures.Add(new DeliveryFailureRow(rd.GetString(0), rd.GetString(1))); }
        }
        return new DeliveryDetail(d, failures);
    }

    private static async Task<DeliverySummary?> LoadDeliveryAsync(NpgsqlConnection conn, string id, EntityScope scope, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand($"{DeliverySelect} WHERE d.id = @id AND {ScopeSql}", conn);
        c.Parameters.AddWithValue("id", id);
        AddScope(c, scope);
        return (await ReadDeliveriesAsync(conn, c, ct)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<DeliverySummary>> ReadDeliveriesAsync(NpgsqlConnection conn, NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<DeliverySummary>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
                int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                rows.Add(new DeliverySummary(r.GetString(0), S(1), S(2), r.GetString(3), DateOnly.FromDateTime(r.GetDateTime(4)), r.GetString(5), r.GetString(6),
                    r.GetString(7).Trim(), I(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
                    r.GetString(11), r.GetInt32(12), r.GetInt32(13), I(14), S(15), S(16), I(17), r.GetBoolean(18), r.GetInt32(19),
                    r.GetFieldValue<string[]>(20), [], [], []));
            }
        }
        if (rows.Count == 0) { return rows; }
        var ids = rows.Select(x => x.Id).ToArray();

        var declared = new Dictionary<string, List<MoneyAmount>>();
        var actual = new Dictionary<string, List<MoneyAmount>>();
        await using (var t = new NpgsqlCommand("SELECT delivery_id, currency, declared_amount, actual_amount FROM delivery_control_totals WHERE delivery_id = ANY(@ids) ORDER BY delivery_id, currency", conn))
        {
            t.Parameters.AddWithValue("ids", ids);
            await using var r = await t.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0); var cur = r.GetString(1).Trim();
                if (!r.IsDBNull(2)) { (declared.TryGetValue(id, out var l) ? l : declared[id] = []).Add(new MoneyAmount(r.GetDecimal(2), cur)); }
                if (!r.IsDBNull(3)) { (actual.TryGetValue(id, out var l) ? l : actual[id] = []).Add(new MoneyAmount(r.GetDecimal(3), cur)); }
            }
        }

        var quals = new Dictionary<string, List<string>>();
        await using (var q = new NpgsqlCommand("""
            SELECT delivery_id, reason_code || ': ' || reason AS q FROM delivery_rejections WHERE delivery_id = ANY(@ids) AND row_number IS NULL
            UNION ALL
            SELECT delivery_id, reason_code || ': ' || count(*) || ' row(s)' FROM delivery_rejections
            WHERE delivery_id = ANY(@ids) AND row_number IS NOT NULL GROUP BY delivery_id, reason_code
            ORDER BY 1, 2
            """, conn))
        {
            q.Parameters.AddWithValue("ids", ids);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { (quals.TryGetValue(r.GetString(0), out var l) ? l : quals[r.GetString(0)] = []).Add(r.GetString(1)); }
        }

        return rows.Select(x => x with
        {
            DeclaredTotals = declared.GetValueOrDefault(x.Id) ?? [],
            ActualTotals = actual.GetValueOrDefault(x.Id) ?? [],
            Qualifications = quals.GetValueOrDefault(x.Id) ?? [],
        }).ToList();
    }

    // ---- records ----

    private static string RecordsSql(string table, string rowIdExpr) => $"""
        SELECT x.grp, x.ord, x.source_row_id, x.rn, x.fields, x.reasons, x.fp,
               COALESCE((SELECT array_agg(e.id ORDER BY e.id) FROM exceptions e WHERE e.delivery_id = @d AND x.source_row_id <> '' AND e.source_row_id = x.source_row_id), ARRAY[]::text[])
        FROM (
          SELECT 0 AS grp, a.id AS ord, a.rowid AS source_row_id, a.rn AS rn, a.j::text AS fields, NULL::text[] AS reasons,
                 encode(sha256(convert_to(a.j::text, 'UTF8')), 'hex') AS fp
          FROM (SELECT t.id, {rowIdExpr} AS rowid, row_number() OVER (ORDER BY t.id) AS rn, to_jsonb(t) - 'id' - 'delivery_id' AS j
                FROM {table} t WHERE t.delivery_id = @d) a
          UNION ALL
          SELECT 1, r.id, COALESCE(r.source_row_id, ''), r.row_number::bigint,
                 jsonb_strip_nulls(jsonb_build_object('raw_row', r.raw_excerpt, 'field', r.field))::text,
                 ARRAY[r.reason_code || ': ' || r.reason],
                 CASE WHEN r.raw_excerpt IS NULL THEN NULL ELSE encode(sha256(convert_to(r.raw_excerpt, 'UTF8')), 'hex') END
          FROM delivery_rejections r WHERE r.delivery_id = @d AND r.row_number IS NOT NULL
        ) x
        """;

    private const string NoTable = "(SELECT NULL::bigint AS id, NULL::text AS delivery_id WHERE false)";

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void Filters(NpgsqlCommand c, RecordQuery q)
    {
        c.Parameters.Add(new NpgsqlParameter("acc", NpgsqlDbType.Boolean) { Value = (object?)q.Accepted ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q.Search) ? DBNull.Value : "%" + EscapeLike(q.Search.Trim()) + "%" });
    }

    private const string FilterSql = """
        (@acc::boolean IS NULL OR ((x.grp = 0) = @acc::boolean))
        AND (@search::text IS NULL OR x.source_row_id ILIKE @search::text OR x.fields ILIKE @search::text)
        """;

    public async Task<RecordPage?> ListRecordsAsync(string deliveryId, RecordQuery query, EntityScope scope, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var d = await LoadDeliveryAsync(conn, deliveryId, scope, ct);
        if (d is null) { return null; }
        var (table, rowId) = Tables.TryGetValue(d.SourceName, out var m) ? m : (NoTable, "''");
        var sql = RecordsSql(table, rowId);

        await using var c = new NpgsqlCommand($"{sql} WHERE {FilterSql} AND (@g::int IS NULL OR (x.grp, x.ord) > (@g::int, @o::bigint)) ORDER BY x.grp, x.ord LIMIT @n", conn);
        c.Parameters.AddWithValue("d", deliveryId);
        Filters(c, query);
        c.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Integer) { Value = (object?)query.AfterGroup ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Bigint) { Value = (object?)query.AfterOrdinal ?? DBNull.Value });
        c.Parameters.AddWithValue("n", query.Limit + 1);
        var items = await ReadRecordsAsync(c, d, ct);

        int? total = null;
        if (query.IncludeTotal)
        {
            await using var tc = new NpgsqlCommand($"SELECT count(*) FROM ({sql} WHERE {FilterSql}) y", conn);
            tc.Parameters.AddWithValue("d", deliveryId);
            Filters(tc, query);
            total = Convert.ToInt32(await tc.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        return new RecordPage(items, total);
    }

    public async Task<(DeliverySummary Delivery, SourceRecordRow Record)?> GetRecordAsync(string recordId, EntityScope scope, CancellationToken ct = default)
    {
        var parts = recordId.Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pk)) { return null; }
        var rejected = parts[0] == RejectedPrefix;
        if (!rejected && !Tables.ContainsKey(parts[0])) { return null; }

        await using var conn = await db.OpenConnectionAsync(ct);
        var lookup = rejected
            ? "SELECT delivery_id FROM delivery_rejections WHERE id = @pk AND row_number IS NOT NULL"
            : $"SELECT delivery_id FROM {Tables[parts[0]].Table} WHERE id = @pk";
        string? deliveryId;
        await using (var l = new NpgsqlCommand(lookup, conn))
        {
            l.Parameters.AddWithValue("pk", pk);
            deliveryId = await l.ExecuteScalarAsync(ct) as string;
        }
        if (deliveryId is null) { return null; }
        var d = await LoadDeliveryAsync(conn, deliveryId, scope, ct);
        if (d is null || (!rejected && d.SourceName != parts[0])) { return null; }

        var (table, rowId) = Tables.TryGetValue(d.SourceName, out var m) ? m : (NoTable, "''");
        await using var c = new NpgsqlCommand($"{RecordsSql(table, rowId)} WHERE x.grp = @g AND x.ord = @o", conn);
        c.Parameters.AddWithValue("d", deliveryId);
        c.Parameters.AddWithValue("g", rejected ? 1 : 0);
        c.Parameters.AddWithValue("o", pk);
        var rec = (await ReadRecordsAsync(c, d, ct)).FirstOrDefault();
        return rec is null ? null : (d, rec);
    }

    private static async Task<IReadOnlyList<SourceRecordRow>> ReadRecordsAsync(NpgsqlCommand c, DeliverySummary d, CancellationToken ct)
    {
        var list = new List<SourceRecordRow>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var grp = r.GetInt32(0); var ord = r.GetInt64(1);
            var fields = new JsonObject();
            foreach (var p in JsonDocument.Parse(r.GetString(4)).RootElement.EnumerateObject())
            {
                // Values as received: amounts keep their exact decimal text, never a float.
                if (p.Value.ValueKind != JsonValueKind.Null) { fields[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText(); }
            }
            list.Add(new SourceRecordRow(grp == 0 ? $"{d.SourceName}:{ord}" : $"{RejectedPrefix}:{ord}", d.SourceName, r.GetString(2), d.Id,
                (int)r.GetInt64(3), grp == 0, fields, r.IsDBNull(5) ? null : r.GetFieldValue<string[]>(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<string[]>(7), grp, ord));
        }
        return list;
    }

    // ---- processing history ----

    public async Task<IReadOnlyList<ProcessingStepRow>?> GetProcessingHistoryAsync(string deliveryId, EntityScope scope, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        if (await LoadDeliveryAsync(conn, deliveryId, scope, ct) is null) { return null; }
        await using var c = new NpgsqlCommand("""
            SELECT id, occurred_at, event_type, actor_type, actor_id, actor_name, summary, reason, after::text
            FROM audit_events WHERE subject_kind = 'DELIVERY' AND subject_id = @d ORDER BY occurred_at, id
            """, conn);
        c.Parameters.AddWithValue("d", deliveryId);
        var steps = new List<ProcessingStepRow>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var type = r.GetString(2);
            var after = S(8) is { } a ? JsonNode.Parse(a) as JsonObject : null;
            var mapped = MapStage(type, after);
            if (mapped is null) { continue; }
            steps.Add(new ProcessingStepRow(r.GetInt64(0).ToString(CultureInfo.InvariantCulture), r.GetFieldValue<DateTimeOffset>(1), mapped.Value.Stage, mapped.Value.Outcome,
                r.GetString(3), S(4) ?? "system", S(5) ?? S(4) ?? "system", S(6) ?? S(7)));
        }
        return steps;
    }

    private static (string Stage, string Outcome)? MapStage(string eventType, JsonObject? after)
    {
        var state = after?["state"]?.GetValue<string>();
        return eventType switch
        {
            "DELIVERY_RECEIVED" when after?["refused"] is not null => ("REJECTED", "ERROR"),
            "DELIVERY_RECEIVED" when state == "REJECTED" => ("REJECTED", "ERROR"),
            "DELIVERY_RECEIVED" when after?["acceptedRecords"] is not null => ("ACCEPTED", state == "QUALIFIED" ? "WARNING" : "OK"),
            "DELIVERY_RECEIVED" => ("RECEIVED", "OK"),
            "RECORD_REJECTED" => ("RECORDS_PARSED", "WARNING"),
            "DELIVERY_SUPERSEDED" => ("SUPERSEDED", "OK"),
            "DELIVERY_INGESTION_RETRIED" => ("RETRIED", "OK"),
            _ => null,
        };
    }
}
