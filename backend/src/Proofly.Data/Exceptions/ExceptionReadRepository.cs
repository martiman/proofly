using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Exceptions;

/// <summary>Read side of the exceptions queue (listExceptions, getExceptionsSummary, getException). Plain parameterised SQL, keyset paging.</summary>
public interface IExceptionReadRepository
{
    /// <summary>Legal entity of the run, or null when the run does not exist.</summary>
    Task<string?> FindRunLegalEntityAsync(string runId, CancellationToken ct = default);
    Task<ExceptionListResult> ListAsync(ExceptionListQuery query, CancellationToken ct = default);
    Task<ExceptionsSummaryData> SummaryAsync(string runId, IReadOnlyCollection<string>? entityScope, ExceptionFilter filter, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Null when the exception does not exist or is outside <paramref name="entityScope"/>.</summary>
    Task<ExceptionDetailData?> GetDetailAsync(string exceptionId, IReadOnlyCollection<string>? entityScope, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Filters JSON of a saved view visible to the user (own or shared, scope 'exceptions'); null when none.</summary>
    Task<string?> FindSavedViewFiltersAsync(string savedViewId, string userId, CancellationToken ct = default);
}

public sealed class ExceptionReadRepository(NpgsqlDataSource db) : IExceptionReadRepository
{
    private const string Terminal = "('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')";
    private const string RowColumns = """
        e.id, e.run_id, e.title, e.control, e.reason_code, e.severity, e.status, e.value_amount, e.value_currency,
        e.legal_entity_id, e.business_date, e.owner_user_id, u.name, e.created_at,
        GREATEST(0, floor(extract(epoch FROM (@now - e.created_at)) / 86400))::int, e.due_at, e.blocking,
        e.source_file, e.source_row_id, e.version
        """;
    private const string From = "FROM exceptions e LEFT JOIN users u ON u.id = e.owner_user_id";

    public async Task<string?> FindRunLegalEntityAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT legal_entity_id FROM runs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", runId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<ExceptionListResult> ListAsync(ExceptionListQuery q, CancellationToken ct = default)
    {
        var keys = q.Sort.Keys;
        if (q.After is not null && q.After.Count != keys.Count) { throw new ArgumentException("Cursor does not match the sort.", nameof(q)); }

        await using var cmd = db.CreateCommand();
        var where = new StringBuilder();
        BuildWhere(cmd, where, q.RunId, q.EntityScope, q.Filter, q.Now);

        var select = new StringBuilder("SELECT ").Append(RowColumns);
        var order = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            var col = ExceptionSortSpec.Columns[keys[i].Name];
            select.Append($", ({col.Sql})::text");
            order.Add($"{col.Sql} {(Desc(keys[i], col) ? "DESC" : "ASC")}");
        }

        var page = new StringBuilder(where.ToString());
        if (q.After is not null) { page.Append(" AND (").Append(KeysetPredicate(cmd, q)).Append(')'); }

        cmd.CommandText = $"{select} {From} WHERE {page} ORDER BY {string.Join(", ", order)} LIMIT @limit";
        cmd.Parameters.AddWithValue("limit", q.Limit + 1);

        var rows = new List<ExceptionRow>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sortKeys = new string[keys.Count];
                for (var i = 0; i < keys.Count; i++) { sortKeys[i] = r.GetString(20 + i); }
                rows.Add(ReadRow(r) with { SortKeys = sortKeys });
            }
        }

        int? total = null;
        if (q.IncludeTotal)
        {
            await using var count = db.CreateCommand();
            var w = new StringBuilder();
            BuildWhere(count, w, q.RunId, q.EntityScope, q.Filter, q.Now);
            count.CommandText = $"SELECT count(*) {From} WHERE {w}";
            total = (int)(long)(await count.ExecuteScalarAsync(ct))!;
        }
        return new ExceptionListResult(rows, total);
    }

    private static bool Desc(SortKey k, SortColumn c) => k.Descending ^ c.Inverted;

    /// <summary>(k0 op v0) OR (k0 = v0 AND k1 op v1) ... with per-key direction; every key expression is NOT NULL.</summary>
    private static string KeysetPredicate(NpgsqlCommand cmd, ExceptionListQuery q)
    {
        var keys = q.Sort.Keys;
        var cols = keys.Select(k => ExceptionSortSpec.Columns[k.Name]).ToList();
        for (var i = 0; i < keys.Count; i++) { cmd.Parameters.AddWithValue($"a{i}", q.After![i]); }
        string P(int i) => $"@a{i}::{PgType(cols[i].Type)}";
        var ors = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            var ands = new List<string>();
            for (var j = 0; j < i; j++) { ands.Add($"{cols[j].Sql} = {P(j)}"); }
            ands.Add($"{cols[i].Sql} {(Desc(keys[i], cols[i]) ? "<" : ">")} {P(i)}");
            ors.Add("(" + string.Join(" AND ", ands) + ")");
        }
        return string.Join(" OR ", ors);
    }

    private static string PgType(SortKeyType t) => t switch
    {
        SortKeyType.Int => "int", SortKeyType.Timestamp => "timestamptz", SortKeyType.Date => "date", SortKeyType.Numeric => "numeric", _ => "text",
    };

    /// <summary>Appends the always-present run, scope and filter conditions. Never emit a query without the scope.</summary>
    private static void BuildWhere(NpgsqlCommand cmd, StringBuilder w, string runId, IReadOnlyCollection<string>? scope, ExceptionFilter f, DateTimeOffset now)
    {
        cmd.Parameters.AddWithValue("now", now);
        w.Append("e.run_id = @run");
        cmd.Parameters.AddWithValue("run", runId);
        AddScope(cmd, w, scope);
        if (f.Severity is { Count: > 0 }) { w.Append(" AND e.severity = ANY(@sev)"); cmd.Parameters.AddWithValue("sev", f.Severity.ToArray()); }
        if (f.Control is { Count: > 0 }) { w.Append(" AND e.control = ANY(@ctl)"); cmd.Parameters.AddWithValue("ctl", f.Control.ToArray()); }
        if (f.Status is { Count: > 0 }) { w.Append(" AND e.status = ANY(@st)"); cmd.Parameters.AddWithValue("st", f.Status.ToArray()); }
        if (f.OwnerId is not null) { w.Append(" AND e.owner_user_id = @owner"); cmd.Parameters.AddWithValue("owner", f.OwnerId); }
        if (f.Unassigned is { } un) { w.Append(un ? " AND e.owner_user_id IS NULL" : " AND e.owner_user_id IS NOT NULL"); }
        if (f.Blocking is { } bl) { w.Append(" AND e.blocking = @blk"); cmd.Parameters.AddWithValue("blk", bl); }
        if (f.LegalEntityId is not null) { w.Append(" AND e.legal_entity_id = @le"); cmd.Parameters.AddWithValue("le", f.LegalEntityId); }
        if (f.Currency is not null) { w.Append(" AND e.value_currency = @cur"); cmd.Parameters.AddWithValue("cur", f.Currency); }
        if (f.ValueMin is { } vmin) { w.Append(" AND abs(e.value_amount) >= @vmin"); cmd.Parameters.Add(new NpgsqlParameter("vmin", NpgsqlDbType.Numeric) { Value = vmin }); }
        if (f.ValueMax is { } vmax) { w.Append(" AND abs(e.value_amount) <= @vmax"); cmd.Parameters.Add(new NpgsqlParameter("vmax", NpgsqlDbType.Numeric) { Value = vmax }); }
        if (f.BusinessDateFrom is { } d1) { w.Append(" AND e.business_date >= @bd1"); cmd.Parameters.Add(new NpgsqlParameter("bd1", NpgsqlDbType.Date) { Value = d1 }); }
        if (f.BusinessDateTo is { } d2) { w.Append(" AND e.business_date <= @bd2"); cmd.Parameters.Add(new NpgsqlParameter("bd2", NpgsqlDbType.Date) { Value = d2 }); }
        const string age = "GREATEST(0, floor(extract(epoch FROM (@now - e.created_at)) / 86400))";
        if (f.AgeDaysMin is { } a1) { w.Append($" AND {age} >= @age1"); cmd.Parameters.AddWithValue("age1", a1); }
        if (f.AgeDaysMax is { } a2) { w.Append($" AND {age} <= @age2"); cmd.Parameters.AddWithValue("age2", a2); }
        if (f.DueBefore is { } due) { w.Append(" AND e.due_at < @due"); cmd.Parameters.AddWithValue("due", due); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            w.Append(" AND (e.id ILIKE @q OR e.title ILIKE @q OR e.reason_code ILIKE @q OR e.source_file ILIKE @q OR e.source_row_id ILIKE @q)");
            cmd.Parameters.AddWithValue("q", "%" + f.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
    }

    private static void AddScope(NpgsqlCommand cmd, StringBuilder w, IReadOnlyCollection<string>? scope)
    {
        if (scope is null) { return; }
        w.Append(" AND e.legal_entity_id = ANY(@scope)");
        cmd.Parameters.AddWithValue("scope", scope.ToArray());
    }

    private static ExceptionRow ReadRow(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9),
        r.GetFieldValue<DateOnly>(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.GetFieldValue<DateTimeOffset>(13), r.GetInt32(14), r.GetFieldValue<DateTimeOffset>(15), r.GetBoolean(16),
        r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18), r.GetInt32(19));

    public async Task<ExceptionsSummaryData> SummaryAsync(string runId, IReadOnlyCollection<string>? entityScope, ExceptionFilter filter, DateTimeOffset now, CancellationToken ct = default)
    {
        NpgsqlCommand Cmd(string select, string? tail = null)
        {
            var cmd = db.CreateCommand();
            var w = new StringBuilder();
            BuildWhere(cmd, w, runId, entityScope, filter, now);
            cmd.CommandText = $"{select} {From} WHERE {w} {tail}";
            return cmd;
        }

        int total, blocking, unassigned, overdue, noValue;
        await using (var c = Cmd($"""
            SELECT count(*), count(*) FILTER (WHERE e.blocking),
                   count(*) FILTER (WHERE e.owner_user_id IS NULL AND e.status NOT IN {Terminal}),
                   count(*) FILTER (WHERE e.due_at < @now AND e.status NOT IN {Terminal}),
                   count(*) FILTER (WHERE e.value_amount IS NULL)
            """))
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (total, blocking, unassigned, overdue, noValue) = ((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4));
        }

        async Task<Dictionary<string, int>> Group(string column)
        {
            var d = new Dictionary<string, int>();
            await using var c = Cmd($"SELECT e.{column}, count(*)", $"GROUP BY e.{column} ORDER BY e.{column}");
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { d[r.GetString(0)] = (int)r.GetInt64(1); }
            return d;
        }

        var bySeverity = await Group("severity");
        var byStatus = await Group("status");
        var byControl = await Group("control");

        var byCurrency = new List<CurrencyAggregate>();
        await using (var c = Cmd("SELECT e.value_currency, count(*), sum(e.value_amount), sum(abs(e.value_amount))",
            "AND e.value_currency IS NOT NULL GROUP BY e.value_currency ORDER BY e.value_currency"))
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { byCurrency.Add(new CurrencyAggregate(r.GetString(0), (int)r.GetInt64(1), r.GetDecimal(2), r.GetDecimal(3))); }
        }

        var byReason = new List<ReasonCount>();
        await using (var c = Cmd("SELECT e.reason_code, count(*)", "GROUP BY e.reason_code ORDER BY count(*) DESC, e.reason_code"))
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { byReason.Add(new ReasonCount(r.GetString(0), (int)r.GetInt64(1))); }
        }

        var bySeverityCurrency = new List<SeverityCurrencyAggregate>();
        await using (var c = Cmd("SELECT e.severity, e.value_currency, count(*), sum(abs(e.value_amount))",
            "AND e.value_currency IS NOT NULL GROUP BY e.severity, e.value_currency " +
            "ORDER BY CASE e.severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END, e.value_currency"))
        await using (var r = await c.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { bySeverityCurrency.Add(new SeverityCurrencyAggregate(r.GetString(0), r.GetString(1).Trim(), (int)r.GetInt64(2), r.GetDecimal(3))); }
        }
        return new ExceptionsSummaryData(total, blocking, unassigned, overdue, noValue, bySeverity, byStatus, byControl, byCurrency, byReason, bySeverityCurrency, now);
    }

    public async Task<ExceptionDetailData?> GetDetailAsync(string exceptionId, IReadOnlyCollection<string>? entityScope, DateTimeOffset now, CancellationToken ct = default)
    {
        ExceptionRow row;
        string runState, ruleset;
        string? deliverySource;
        var w = new StringBuilder("e.id = @id");
        await using (var cmd = db.CreateCommand())
        {
            cmd.Parameters.AddWithValue("id", exceptionId);
            cmd.Parameters.AddWithValue("now", now);
            AddScope(cmd, w, entityScope);
            cmd.CommandText = $"""
                SELECT {RowColumns}, ru.state, ru.ruleset_version, sd.source_name
                {From} JOIN runs ru ON ru.id = e.run_id LEFT JOIN source_deliveries sd ON sd.id = e.delivery_id
                WHERE {w}
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { return null; }
            row = ReadRow(r);
            runState = r.GetString(20);
            ruleset = r.GetString(21);
            deliverySource = r.IsDBNull(22) ? null : r.GetString(22);
        }

        var evidence = new List<EvidenceRefRow>();
        await using (var cmd = db.CreateCommand("SELECT id, kind, ref FROM exception_evidence_refs WHERE exception_id = @id ORDER BY id"))
        {
            cmd.Parameters.AddWithValue("id", exceptionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { evidence.Add(new EvidenceRefRow(r.GetInt64(0), r.GetString(1), r.GetString(2))); }
        }

        var timeline = new List<TimelineRow>();
        await using (var cmd = db.CreateCommand("""
            SELECT t.id, t.kind, t.actor_type, t.actor_id, u.name, t.summary, t.occurred_at
            FROM exception_timeline t LEFT JOIN users u ON u.id = t.actor_id
            WHERE t.exception_id = @id ORDER BY t.occurred_at, t.id
            """))
        {
            cmd.Parameters.AddWithValue("id", exceptionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                timeline.Add(new TimelineRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        var approvals = new List<ApprovalRow>();
        await using (var cmd = db.CreateCommand("""
            SELECT a.id, a.object_type, a.object_id, a.run_id, a.proposal_version, a.proposer_user_id, pu.name,
                   a.approver_user_id, au.name, a.required_role, a.status, a.decision, a.rationale, a.evidence_refs::text,
                   a.policy_version, a.proposed_at, a.decided_at
            FROM approvals a
            JOIN users pu ON pu.id = a.proposer_user_id
            LEFT JOIN users au ON au.id = a.approver_user_id
            WHERE a.object_type = 'EXCEPTION_RESOLUTION'
              AND (a.object_id = @id OR a.object_id IN (SELECT p.id FROM resolution_proposals p WHERE p.exception_id = @id))
            ORDER BY a.proposed_at, a.id
            """))
        {
            cmd.Parameters.AddWithValue("id", exceptionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var refs = JsonSerializer.Deserialize<string[]>(r.GetString(13)) ?? [];
                approvals.Add(new ApprovalRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4),
                    r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                    r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), refs,
                    r.GetString(14), r.GetFieldValue<DateTimeOffset>(15), r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16)));
            }
        }

        string? policy;
        await using (var cmd = db.CreateCommand("SELECT config_version FROM config_rules WHERE kind = 'approval_policy' AND enabled ORDER BY updated_at DESC, id LIMIT 1"))
        {
            policy = await cmd.ExecuteScalarAsync(ct) as string;
        }

        RuleTraceRow? trace = null;
        await using (var cmd = db.CreateCommand("SELECT rule_id, rule_version, tolerance, expected_effect, description, calculation::text FROM exception_rule_traces WHERE exception_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", exceptionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                trace = new RuleTraceRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5));
            }
        }
        return new ExceptionDetailData(row, runState, ruleset, deliverySource, evidence, timeline, approvals, policy, trace);
    }

    public async Task<string?> FindSavedViewFiltersAsync(string savedViewId, string userId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT filters::text FROM saved_views WHERE id = @id AND scope = 'exceptions' AND (user_id = @u OR shared)");
        cmd.Parameters.AddWithValue("id", savedViewId);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
