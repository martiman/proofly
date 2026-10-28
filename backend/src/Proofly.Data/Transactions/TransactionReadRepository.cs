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
    Task<AccountRow?> GetAccountAsync(string runId, string accountId, CancellationToken ct = default);
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

    private static (string Where, List<NpgsqlParameter> Params) TxWhere(TransactionFilter f)
    {
        var w = new List<string>();
        var p = new List<NpgsqlParameter>();
        void Add(string cond, string name, object value, NpgsqlDbType type) { w.Add(cond); p.Add(new NpgsqlParameter(name, type) { Value = value }); }
        if (f.Kind is not null) { Add("kind = @kind", "kind", f.Kind, NpgsqlDbType.Text); }
        if (f.MatchStatus is not null) { Add("match_status = @mstatus", "mstatus", f.MatchStatus, NpgsqlDbType.Text); }
        if (f.AccountId is not null) { Add("account_id = @acct", "acct", f.AccountId, NpgsqlDbType.Text); }
        if (f.Currency is not null) { Add("currency = @ccy", "ccy", f.Currency, NpgsqlDbType.Text); }
        if (f.HasException is { } he) { w.Add(he ? "cardinality(exception_ids) > 0" : "cardinality(exception_ids) = 0"); }
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
        var (where, ps) = TxWhere(filter);
        var cte = "WITH " + TransactionSql.TxCte + ", f AS (SELECT * FROM tx" + where + ")";

        var sb = new StringBuilder(cte).Append(" SELECT ").Append(TransactionSql.TxColumns).Append(" FROM f");
        if (after is not null) { sb.Append(" WHERE (ts, id) > (@c_ts, @c_id)"); }
        sb.Append(" ORDER BY ts, id LIMIT @lim");

        var rows = new List<TransactionRow>();
        await using (var cmd = db.CreateCommand(sb.ToString()))
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
        await using (var cmd = db.CreateCommand(cte + """
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
        foreach (var p in ps) { cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value }); }
    }

    private static TransactionRow ReadTx(NpgsqlDataReader r, string runId)
    {
        var id = r.GetString(0);
        var deliveryId = r.GetString(4);
        var sourceRowId = r.GetString(3);
        return new TransactionRow(id, runId, r.GetString(1), r.GetString(2), sourceRowId, RecordId(deliveryId, sourceRowId), deliveryId,
            r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetDecimal(8), r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetFieldValue<string[]>(12), r.GetString(13),
            r.IsDBNull(14) ? null : r.GetString(14), r.GetFieldValue<string[]>(15), r.GetBoolean(16));
    }

    /// <summary>Stable record id for GET /source-records/{recordId}: "{deliveryId}/{sourceRowId}".</summary>
    public static string RecordId(string deliveryId, string sourceRowId) => deliveryId + "/" + sourceRowId;

    public async Task<TransactionRow?> GetTransactionAsync(string runId, string transactionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("WITH " + TransactionSql.TxCte + " SELECT " + TransactionSql.TxColumns + " FROM tx WHERE id = @id");
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
        var deliveries = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static string NodeKind(string kind) => kind switch { "BANK_EVIDENCE" => "BANK_TRANSACTION", "LEDGER_ENTRY" => "LEDGER_ENTRY", _ => "INSTRUCTION" };
        void AddTx(TransactionRow t)
        {
            if (!seen.Add(t.Id)) { return; }
            nodes.Add(new LineageNode(t.Id, NodeKind(t.Kind), t.Reference, t.Source, t.SourceRowId, t.RecordId, t.Amount, t.Currency));
        }
        AddTx(subject);

        // Related records: share the instruction/reference key (own key, linked key, or linked by the other record).
        await using (var cmd = db.CreateCommand("""
            WITH 
            """ + TransactionSql.TxCte + """

            , me AS (SELECT key_a, key_b FROM tx WHERE id = @id)
            SELECT t.id, t.kind, t.source, t.source_row_id, t.delivery_id, t.reference, t.amount, t.currency, t.fingerprint
              FROM tx t, me
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
                    nodes.Add(new LineageNode(id, NodeKind(r.GetString(1)), r.GetString(5), r.GetString(2), r.GetString(3), RecordId(r.GetString(4), r.GetString(3)), r.GetDecimal(6), r.GetString(7)));
                }
                edges.Add(new LineageEdge(id, subject.Id, "LINKED_BY_REFERENCE"));
                deliveries.Add(r.GetString(4));
            }
        }

        deliveries.Add(subject.DeliveryId);
        var deliveryRows = new List<LineageDelivery>();
        await using (var cmd = db.CreateCommand("SELECT id, fingerprint_sha256, schema_version FROM source_deliveries WHERE run_id = @run AND id = ANY(@d) ORDER BY id"))
        {
            cmd.Parameters.AddWithValue("run", runId);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = deliveries.ToArray() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { deliveryRows.Add(new LineageDelivery(r.GetString(0), r.GetString(1), r.GetString(2))); }
        }
        foreach (var d in deliveryRows)
        {
            nodes.Add(new LineageNode("DLV-" + d.DeliveryId, "DELIVERY", d.DeliveryId, "source_deliveries", d.DeliveryId, null, null, null));
        }
        foreach (var n in nodes.Where(n => n.Kind != "DELIVERY" && n.Kind != "CONTROL_RESULT").ToList())
        {
            var did = n.RecordId![..n.RecordId!.LastIndexOf('/')];
            edges.Add(new LineageEdge("DLV-" + did, n.Id, "DELIVERED"));
        }

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

    private const string AccColumns = "account_id, currency, legal_entity_id, opening, recorded, independent, credits, debits, state, exception_ids";

    private static AccountRow ReadAcc(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4),
        r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetString(8), r.GetFieldValue<string[]>(9));

    public async Task<AccountPageResult> ListAccountsAsync(string runId, AccountFilter filter, AccountCursor? after, int limit, CancellationToken ct = default)
    {
        var (where, ps) = AccWhere(filter);
        var cte = "WITH " + TransactionSql.TxCte + ", " + TransactionSql.AccCte + ", f AS (SELECT * FROM acc" + where + ")";
        var sql = cte + " SELECT " + AccColumns + " FROM f" + (after is null ? "" : " WHERE (account_id, currency) > (@c_a, @c_c)") + " ORDER BY account_id, currency LIMIT @lim";
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
        await using (var cmd = db.CreateCommand(cte + " SELECT currency, count(*), COALESCE(SUM(recorded - independent), 0) FROM f GROUP BY currency ORDER BY currency"))
        {
            Bind(cmd, runId, ps);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { aggs.Add(new AccountAggregate(r.GetString(0), (int)r.GetInt64(1), r.GetDecimal(2))); }
        }
        return new AccountPageResult(rows, hasMore, aggs);
    }

    /// <summary>When the account exists in several currencies, the lowest currency code is returned (contract addresses accounts by id only).</summary>
    public async Task<AccountRow?> GetAccountAsync(string runId, string accountId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("WITH " + TransactionSql.TxCte + ", " + TransactionSql.AccCte +
            " SELECT " + AccColumns + " FROM acc WHERE account_id = @acct ORDER BY currency LIMIT 1");
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("acct", accountId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAcc(r) : null;
    }

    public async Task<LedgerEntryPage> ListLedgerEntriesAsync(string runId, string accountId, string currency, LedgerCursor? after, int limit, CancellationToken ct = default)
    {
        var sql = """
            SELECT le.id, le.account_id, le.booking_timestamp, le.value_date, le.debit_credit_indicator, le.amount, le.currency::text,
                   COALESCE(le.transaction_reference, le.ledger_entry_id), le.entry_type, le.source_status, le.delivery_id, le.ledger_entry_id,
                   COALESCE((SELECT array_agg(e.id ORDER BY e.id) FROM exceptions e
                              WHERE e.run_id = @run AND e.delivery_id = le.delivery_id AND e.source_row_id = le.ledger_entry_id), ARRAY[]::text[])
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
                RecordId(r.GetString(10), r.GetString(11)), r.GetFieldValue<string[]>(12), internalId));
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
                   e.business_date, e.owner_user_id, u.name, e.created_at, e.due_at, e.blocking, COALESCE(e.source_file, ''), COALESCE(e.source_row_id, ''), e.version
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
                r.GetFieldValue<DateTimeOffset>(14), r.GetBoolean(15), r.GetString(16), r.GetString(17), r.GetInt32(18)));
        }
        return rows;
    }
}
