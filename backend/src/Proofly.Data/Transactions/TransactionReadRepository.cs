using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Transactions;

public interface ITransactionReadRepository
{
    Task<RunScope?> FindRunAsync(string runId, CancellationToken ct = default);
    Task<TransactionPageResult> ListTransactionsAsync(string runId, TransactionFilter filter, TransactionCursor? after, int limit, bool includeTotal, CancellationToken ct = default);
    Task<TransactionRow?> GetTransactionAsync(string runId, string transactionId, CancellationToken ct = default);
    Task<TransactionLineage?> GetLineageAsync(string runId, string transactionId, CancellationToken ct = default);
    Task<AccountPageResult> ListAccountsAsync(string runId, AccountFilter filter, AccountCursor? after, int limit, CancellationToken ct = default);
    /// <summary>Every currency position of the account in the run (or only <paramref name="currency"/>); empty when none.</summary>
    Task<IReadOnlyList<AccountRow>> FindAccountAsync(string runId, string accountId, string? currency, CancellationToken ct = default);
    Task<LedgerEntryPage> ListLedgerEntriesAsync(string runId, string accountId, string currency, LedgerCursor? after, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<AccountExceptionRow>> ListExceptionsAsync(string runId, IReadOnlyCollection<string> exceptionIds, CancellationToken ct = default);
}

/// <summary>Read-only, run-scoped queries. Legal-entity scope is enforced by the caller through <see cref="FindRunAsync"/> before any other call.</summary>
public sealed class TransactionReadRepository(NpgsqlDataSource db) : ITransactionReadRepository
{
    public async Task<RunScope?> FindRunAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT legal_entity_id, business_date FROM runs WHERE id = @run");
        cmd.Parameters.AddWithValue("run", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new RunScope(runId, r.GetString(0), DateOnly.FromDateTime(r.GetDateTime(1))) : null;
    }

    private static string Like(string s) => "%" + s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Filter on the cheap (record) columns only, or on every column when <paramref name="enrichedToo"/> (match status and exception presence need the enrichment).</summary>
    private static (string Where, List<NpgsqlParameter> Params) TxWhere(TransactionFilter f, bool enrichedToo = true)
    {
        var w = new List<string>();
        var p = new List<NpgsqlParameter>();
        void Add(string cond, string name, object value, NpgsqlDbType type) { w.Add(cond); p.Add(new NpgsqlParameter(name, type) { Value = value }); }
        if (f.Kind is not null) { Add("kind = @kind", "kind", f.Kind, NpgsqlDbType.Text); }
        if (enrichedToo && f.MatchStatus is not null) { Add("match_status = @mstatus", "mstatus", f.MatchStatus, NpgsqlDbType.Text); }
        if (f.AccountId is not null) { Add("account_id = @acct", "acct", f.AccountId, NpgsqlDbType.Text); }
        if (f.Currency is not null) { Add("currency = @ccy", "ccy", f.Currency, NpgsqlDbType.Text); }
        if (enrichedToo && f.HasException is { } he) { w.Add(he ? "cardinality(exception_ids) > 0" : "cardinality(exception_ids) = 0"); }
        if (f.From is { } from) { Add("ts >= @from", "from", from.UtcDateTime, NpgsqlDbType.TimestampTz); }
        if (f.To is { } to) { Add("ts <= @to", "to", to.UtcDateTime, NpgsqlDbType.TimestampTz); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            Add("(reference ILIKE @q OR source_row_id ILIKE @q OR id ILIKE @q OR COALESCE(account_id, '') ILIKE @q)", "q", Like(f.Search.Trim()), NpgsqlDbType.Text);
        }
        return (w.Count == 0 ? "" : " WHERE " + string.Join(" AND ", w), p);
    }

    public async Task<TransactionPageResult> ListTransactionsAsync(string runId, TransactionFilter filter, TransactionCursor? after, int limit, bool includeTotal, CancellationToken ct = default)
    {
        // Filters on the cheap record columns (the usual case) select and order the page FIRST, then enrich only its rows (lifecycle, match,
        // exceptions are per-row index lookups). A filter on match status or exception presence needs every row enriched, so it enriches the
        // whole run first (the earlier behaviour); results are identical either way.
        var pageFirst = filter.MatchStatus is null && filter.HasException is null;
        var (where, ps) = TxWhere(filter, enrichedToo: !pageFirst);
        string pageSql, aggBase;
        if (pageFirst)
        {
            var cursor = after is null ? "" : (where.Length == 0 ? " WHERE" : " AND") + " (ts, id) > (@c_ts, @c_id)";
            pageSql = "WITH " + TransactionSql.SrcCte + ", sel AS (SELECT * FROM src_run" + where + cursor + " ORDER BY ts, id LIMIT @lim), tx AS ("
                + TransactionSql.Enrich("sel") + ") SELECT " + TransactionSql.TxColumns + " FROM tx ORDER BY ts, id";
            aggBase = "WITH " + TransactionSql.SrcCte + ", f AS (SELECT * FROM src_run" + where + ")";
        }
        else
        {
            var cte = "WITH " + TransactionSql.TxCte + ", f AS (SELECT * FROM tx" + where + ")";
            pageSql = cte + " SELECT " + TransactionSql.TxColumns + " FROM f" + (after is null ? "" : " WHERE (ts, id) > (@c_ts, @c_id)") + " ORDER BY ts, id LIMIT @lim";
            aggBase = cte;
        }

        var rows = new List<TransactionRow>();
        await using (var cmd = db.CreateCommand(pageSql))
        {
            Bind(cmd, runId, ps);
            if (after is not null)
            {
                cmd.Parameters.Add(new NpgsqlParameter("c_ts", NpgsqlDbType.TimestampTz) { Value = after.Ts.UtcDateTime });
                cmd.Parameters.AddWithValue("c_id", after.Id);
            }
            cmd.Parameters.AddWithValue("lim", limit + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { rows.Add(ReadTx(r, runId)); }
        }
        var hasMore = rows.Count > limit;
        if (hasMore) { rows.RemoveAt(rows.Count - 1); }

        // Aggregates over the full filtered set (not the page), per currency, NUMERIC arithmetic in SQL.
        var aggs = new List<CurrencyAggregate>();
        await using (var cmd = db.CreateCommand(aggBase + """
             SELECT currency, count(*), COALESCE(SUM(amount) FILTER (WHERE direction = 'DEBIT'), 0),
                    COALESCE(SUM(amount) FILTER (WHERE direction = 'CREDIT'), 0)
               FROM f GROUP BY currency ORDER BY currency
            """))
        {
            Bind(cmd, runId, ps);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { aggs.Add(new CurrencyAggregate(r.GetString(0), (int)r.GetInt64(1), r.GetDecimal(2), r.GetDecimal(3))); }
        }
        return new TransactionPageResult(rows, hasMore, includeTotal ? aggs.Sum(a => a.Count) : null, aggs);
    }

    private static void Bind(NpgsqlCommand cmd, string runId, List<NpgsqlParameter> ps)
    {
        cmd.Parameters.AddWithValue("run", runId);
        if (cmd.CommandText.Contains("@acct::text", StringComparison.Ordinal) && !ps.Any(p => p.ParameterName == "acct"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("acct", NpgsqlDbType.Text) { Value = DBNull.Value }); // all accounts
        }
        foreach (var p in ps) { cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value }); }
    }

    private static TransactionRow ReadTx(NpgsqlDataReader r, string runId)
    {
        var id = r.GetString(0);
        var deliveryId = r.GetString(4);
        var sourceRowId = r.GetString(3);
        return new TransactionRow(id, runId, r.GetString(1), r.GetString(2), sourceRowId, RecordId(r.GetString(2), r.GetInt64(21)), deliveryId,
            r.IsDBNull(20) ? null : r.GetInt32(20), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetDecimal(8), r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetFieldValue<string[]>(12), r.GetString(13),
            r.IsDBNull(14) ? null : r.GetString(14), r.GetFieldValue<string[]>(15), r.GetBoolean(16));
    }

    /// <summary>Record id for GET /source-records/{recordId}: "{sourceName}:{pk}" (see the API README, Identifiers).</summary>
    public static string RecordId(string sourceName, long pk) => sourceName + ":" + pk.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<TransactionRow?> GetTransactionAsync(string runId, string transactionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("WITH " + TransactionSql.SrcCte + ", sel AS (SELECT * FROM src_run WHERE id = @id), tx AS (" + TransactionSql.Enrich("sel")
            + ") SELECT " + TransactionSql.TxColumns + " FROM tx");
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("id", transactionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadTx(r, runId) : null;
    }

    public async Task<TransactionLineage?> GetLineageAsync(string runId, string transactionId, CancellationToken ct = default)
    {
        var subject = await GetTransactionAsync(runId, transactionId, ct);
        if (subject is null) { return null; }

        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();
        var nodeDelivery = new Dictionary<string, string>(StringComparer.Ordinal);   // node id -> delivery id
        var rowNumbers = new Dictionary<string, int>(StringComparer.Ordinal);       // delivery id -> first known row
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static string NodeKind(string kind) => kind switch { "BANK_EVIDENCE" => "BANK_TRANSACTION", "LEDGER_ENTRY" => "LEDGER_ENTRY", _ => "INSTRUCTION" };
        void Track(string nodeId, string deliveryId, int? row)
        {
            nodeDelivery[nodeId] = deliveryId;
            if (row is { } rn && !rowNumbers.ContainsKey(deliveryId)) { rowNumbers[deliveryId] = rn; }
        }
        void AddTx(TransactionRow t)
        {
            if (!seen.Add(t.Id)) { return; }
            nodes.Add(new LineageNode(t.Id, NodeKind(t.Kind), t.Reference, t.Source, t.SourceRowId, t.RecordId, t.Amount, t.Currency));
            Track(t.Id, t.DeliveryId, t.RowNumber);
        }
        AddTx(subject);

        // Related records: share the instruction/reference key (own key, linked key, or linked by the other record).
        await using (var cmd = db.CreateCommand("""
            WITH 
            """ + TransactionSql.SrcCte + """

            , me AS (SELECT key_a, key_b FROM src_run WHERE id = @id)
            SELECT t.id, t.kind, t.source, t.source_row_id, t.delivery_id, t.reference, t.amount, t.currency, t.fingerprint, t.rn, t.rid
              FROM src_run t, me
             WHERE t.id <> @id
               AND ((t.key_a IS NOT NULL AND t.key_a IN (me.key_a, me.key_b))
                    OR (t.key_b IS NOT NULL AND t.key_b IN (me.key_a, me.key_b)))
             ORDER BY t.ts, t.id LIMIT 50
            """))
        {
            cmd.Parameters.AddWithValue("run", runId);
            cmd.Parameters.AddWithValue("id", transactionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                if (seen.Add(id))
                {
                    nodes.Add(new LineageNode(id, NodeKind(r.GetString(1)), r.GetString(5), r.GetString(2), r.GetString(3), RecordId(r.GetString(2), r.GetInt64(10)), r.GetDecimal(6), r.GetString(7)));
                    Track(id, r.GetString(4), r.IsDBNull(9) ? null : r.GetInt32(9));
                }
                edges.Add(new LineageEdge(id, subject.Id, "LINKED_BY_REFERENCE"));
            }
        }

        var deliveryRows = new List<LineageDelivery>();
        await using (var cmd = db.CreateCommand("SELECT id, fingerprint_sha256, schema_version FROM source_deliveries WHERE run_id = @run AND id = ANY(@d) ORDER BY id"))
        {
            cmd.Parameters.AddWithValue("run", runId);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = nodeDelivery.Values.Distinct().ToArray() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                deliveryRows.Add(new LineageDelivery(id, r.GetString(1), r.GetString(2), rowNumbers.TryGetValue(id, out var rn) ? rn : null));
            }
        }
        foreach (var d in deliveryRows)
        {
            nodes.Add(new LineageNode("DLV-" + d.DeliveryId, "DELIVERY", d.DeliveryId, "source_deliveries", d.DeliveryId, null, null, null));
        }
        foreach (var (nodeId, deliveryId) in nodeDelivery) { edges.Add(new LineageEdge("DLV-" + deliveryId, nodeId, "DELIVERED")); }

        foreach (var exId in subject.ExceptionIds)
        {
            nodes.Add(new LineageNode("EXC-" + exId, "CONTROL_RESULT", exId, "exceptions", exId, null, null, null));
            edges.Add(new LineageEdge(subject.Id, "EXC-" + exId, "RAISED"));
        }
        return new TransactionLineage(subject.Id, nodes, edges, deliveryRows);
    }

    // ---- accounts ----

    private static (string Where, List<NpgsqlParameter> Params) AccWhere(AccountFilter f)
    {
        var w = new List<string>();
        var p = new List<NpgsqlParameter>();
        if (f.Currency is not null) { w.Add("currency = @ccy"); p.Add(new NpgsqlParameter("ccy", NpgsqlDbType.Text) { Value = f.Currency }); }
        if (f.State is not null) { w.Add("state = @state"); p.Add(new NpgsqlParameter("state", NpgsqlDbType.Text) { Value = f.State }); }
        if (!string.IsNullOrWhiteSpace(f.Search)) { w.Add("account_id ILIKE @q"); p.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = Like(f.Search.Trim()) }); }
        return (w.Count == 0 ? "" : " WHERE " + string.Join(" AND ", w), p);
    }

    private const string AccColumns = "account_id, currency, legal_entity_id, opening, recorded, independent, credits, debits, adjustments, difference, state, exception_ids";

    private static decimal? Dec(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);

    private static AccountRow ReadAcc(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1).Trim(), r.GetString(2), Dec(r, 3), Dec(r, 4), Dec(r, 5),
        r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), Dec(r, 9), r.GetString(10), r.GetFieldValue<string[]>(11));

    public async Task<AccountPageResult> ListAccountsAsync(string runId, AccountFilter filter, AccountCursor? after, int limit, CancellationToken ct = default)
    {
        // Without a state filter the page of accounts is selected and ordered from the balances alone, and only its accounts get their
        // exceptions and state resolved (they are the expensive part). A state filter needs the state of every account, so it resolves all
        // of them (the earlier behaviour). The per-currency aggregates (account count, total difference) never need the state unless it is filtered on.
        var pageFirst = filter.State is null;
        var (where, ps) = AccWhere(filter);
        string sql, aggBase;
        if (pageFirst)
        {
            var cursor = after is null ? "" : (where.Length == 0 ? " WHERE" : " AND") + " (account_id, currency) > (@c_a, @c_c)";
            sql = "WITH " + TransactionSql.SrcCte + ", " + TransactionSql.AccBaseCte + ", pg AS (SELECT * FROM acc_base" + where + cursor
                + " ORDER BY account_id, currency LIMIT @lim), sc AS (SELECT array_agg(account_id) AS accts FROM pg), "
                + TransactionSql.AccExCte("pg", "s.account_id = ANY((SELECT accts FROM sc)::text[])") + " SELECT " + AccColumns + " FROM acc ORDER BY account_id, currency";
            aggBase = "WITH " + TransactionSql.AccBaseCte + ", f AS (SELECT * FROM acc_base" + where + ")";
        }
        else
        {
            var cte = "WITH " + TransactionSql.SrcCte + ", " + TransactionSql.AccCte + ", f AS (SELECT * FROM acc" + where + ")";
            sql = cte + " SELECT " + AccColumns + " FROM f" + (after is null ? "" : " WHERE (account_id, currency) > (@c_a, @c_c)") + " ORDER BY account_id, currency LIMIT @lim";
            aggBase = cte;
        }
        var rows = new List<AccountRow>();
        await using (var cmd = db.CreateCommand(sql))
        {
            Bind(cmd, runId, ps);
            if (after is not null) { cmd.Parameters.AddWithValue("c_a", after.AccountId); cmd.Parameters.AddWithValue("c_c", after.Currency); }
            cmd.Parameters.AddWithValue("lim", limit + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { rows.Add(ReadAcc(r)); }
        }
        var hasMore = rows.Count > limit;
        if (hasMore) { rows.RemoveAt(rows.Count - 1); }

        var aggs = new List<AccountAggregate>();
        await using (var cmd = db.CreateCommand(aggBase + " SELECT currency, count(*), COALESCE(SUM(difference), 0) FROM f GROUP BY currency ORDER BY currency"))
        {
            Bind(cmd, runId, ps);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { aggs.Add(new AccountAggregate(r.GetString(0), (int)r.GetInt64(1), r.GetDecimal(2))); }
        }
        return new AccountPageResult(rows, hasMore, aggs);
    }

    public async Task<IReadOnlyList<AccountRow>> FindAccountAsync(string runId, string accountId, string? currency, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("WITH " + TransactionSql.SrcCte + ", " + TransactionSql.AccCte +
            " SELECT " + AccColumns + " FROM acc WHERE account_id = @acct AND (@ccy::text IS NULL OR currency = @ccy) ORDER BY currency");
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.Add(new NpgsqlParameter("acct", NpgsqlDbType.Text) { Value = accountId });
        cmd.Parameters.Add(new NpgsqlParameter("ccy", NpgsqlDbType.Text) { Value = (object?)currency ?? DBNull.Value });
        var rows = new List<AccountRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { rows.Add(ReadAcc(r)); }
        return rows;
    }

    public async Task<LedgerEntryPage> ListLedgerEntriesAsync(string runId, string accountId, string currency, LedgerCursor? after, int limit, CancellationToken ct = default)
    {
        var sql = """
            SELECT le.id, le.account_id, le.booking_timestamp, le.value_date, le.debit_credit_indicator, le.amount, le.currency::text,
                   COALESCE(le.transaction_reference, le.ledger_entry_id), le.entry_type, le.source_status,
                   COALESCE((SELECT array_agg(x.id ORDER BY x.id) FROM (
                               SELECT e.id FROM exceptions e WHERE e.run_id = @run AND e.delivery_id = le.delivery_id AND e.source_row_id = le.ledger_entry_id
                               UNION
                               SELECT e.id FROM exceptions e JOIN exception_links l ON l.exception_id = e.id
                                WHERE e.run_id = @run AND l.record_table = 'ledger_entries' AND l.record_id = le.id) x), ARRAY[]::text[])
              FROM ledger_entries le
              JOIN source_deliveries d ON d.id = le.delivery_id AND d.run_id = @run
             WHERE le.account_id = @acct AND le.currency = @ccy
            """ + (after is null ? "" : " AND (le.booking_timestamp, le.id) > (@c_ts, @c_id)") + " ORDER BY le.booking_timestamp, le.id LIMIT @lim";
        var rows = new List<LedgerEntryRow>();
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("acct", accountId);
        cmd.Parameters.AddWithValue("ccy", currency);
        if (after is not null)
        {
            cmd.Parameters.Add(new NpgsqlParameter("c_ts", NpgsqlDbType.TimestampTz) { Value = after.Ts.UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter("c_id", NpgsqlDbType.Bigint) { Value = after.Id });
        }
        cmd.Parameters.AddWithValue("lim", limit + 1);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var internalId = r.GetInt64(0);
            rows.Add(new LedgerEntryRow("LED-" + internalId, r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), DateOnly.FromDateTime(r.GetDateTime(3)),
                r.GetString(4), r.GetDecimal(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
                RecordId("internal_ledger_entries", internalId), r.GetFieldValue<string[]>(10), internalId));
        }
        var hasMore = rows.Count > limit;
        if (hasMore) { rows.RemoveAt(rows.Count - 1); }
        return new LedgerEntryPage(rows, hasMore);
    }

    public async Task<IReadOnlyList<AccountExceptionRow>> ListExceptionsAsync(string runId, IReadOnlyCollection<string> exceptionIds, CancellationToken ct = default)
    {
        var rows = new List<AccountExceptionRow>();
        if (exceptionIds.Count == 0) { return rows; }
        await using var cmd = db.CreateCommand("""
            SELECT e.id, e.run_id, e.title, e.control, e.reason_code, e.severity, e.status, e.value_amount, e.value_currency::text, e.legal_entity_id,
                   e.business_date, e.owner_user_id, u.name, e.created_at, e.due_at, e.blocking, e.source_file, e.source_row_id, e.version
              FROM exceptions e LEFT JOIN users u ON u.id = e.owner_user_id
             WHERE e.run_id = @run AND e.id = ANY(@ids) ORDER BY e.id
            """);
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = exceptionIds.ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new AccountExceptionRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), DateOnly.FromDateTime(r.GetDateTime(10)),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetFieldValue<DateTimeOffset>(13),
                r.GetFieldValue<DateTimeOffset>(14), r.GetBoolean(15), r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17), r.GetInt32(18)));
        }
        return rows;
    }
}
