using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Proofly.Domain.Ids;

namespace Proofly.Data.Matching;

/// <summary>Read-side access to match_groups / match_candidates. Callers must have checked legal-entity scope via <see cref="GetRunScopeAsync"/>; every query is also bound to the run id.</summary>
public interface IMatchingRepository
{
    Task<RunScope?> GetRunScopeAsync(string runId, CancellationToken ct);
    /// <summary>Keyset page ordered by group id; fetches up to <paramref name="limit"/> + 1 rows.</summary>
    Task<IReadOnlyList<MatchGroupRow>> ListGroupsAsync(string runId, string? status, int limit, string? afterId, CancellationToken ct);
    Task<long> CountGroupsAsync(string runId, string? status, CancellationToken ct);
    Task<MatchGroupRow?> GetGroupAsync(string runId, string groupId, CancellationToken ct);
    /// <summary>
    /// Resolves the focus (transaction, exception or first open group). A transaction/exception that exists but is in no group yields
    /// (null, transaction-or-null, None); one that does not exist in the run yields NotFound.
    /// </summary>
    Task<(string? GroupId, string? FocusTransactionId, FocusFailure Failure)> ResolveFocusAsync(string runId, string? transactionId, string? exceptionId, CancellationToken ct);
    /// <summary>Unmatched candidates ordered by (group id, candidate id), after the keyset position; fetches exactly <paramref name="limit"/> rows (callers ask for one extra to detect a next page).</summary>
    Task<IReadOnlyList<MatchCandidateRow>> ListUnmatchedAsync(string runId, int limit, CancellationToken ct, UnmatchedFilter? filter = null, UnmatchedCursorState? after = null);
    Task<long> CountUnmatchedAsync(string runId, UnmatchedFilter? filter, CancellationToken ct);
    Task<MatchingStats> GetStatsAsync(string runId, CancellationToken ct);
}

public sealed class MatchingRepository(NpgsqlDataSource db) : IMatchingRepository
{
    public const int UnmatchedLimit = 200;
    public const int DefaultInlineUnmatched = 50;

    /// <summary>Public transaction id of a canonical record (see <see cref="TransactionIds"/>).</summary>
    public static string TransactionId(string table, long id) => TransactionIds.Format(table, id);

    // Candidate with its canonical record resolved (timestamp, reference, row key used by exceptions).
    private const string CandidateSelect = """
        SELECT c.id AS cid, c.match_group_id, c.kind, c.record_table, c.record_id, c.amount, c.currency, c.score, c.selected, c.side,
               r.delivery_id, r.row_key, r.ts, r.ref,
               COALESCE(sd.source_name, c.record_table) AS source_name,
               ARRAY(SELECT x.id FROM (
                       SELECT e.id FROM exceptions e WHERE e.run_id = g.run_id AND e.delivery_id = r.delivery_id AND e.source_row_id = r.row_key
                       UNION
                       SELECT e.id FROM exceptions e JOIN exception_links l ON l.exception_id = e.id
                        WHERE e.run_id = g.run_id AND l.record_table = c.record_table AND l.record_id = c.record_id) x ORDER BY x.id) AS exception_ids
        FROM match_candidates c
        JOIN match_groups g ON g.id = c.match_group_id
        LEFT JOIN LATERAL (
            SELECT delivery_id, source_row_id AS row_key, instruction_timestamp AS ts, COALESCE(external_reference, instruction_id) AS ref FROM transfer_instructions WHERE c.record_table = 'transfer_instructions' AND id = c.record_id
            UNION ALL
            SELECT delivery_id, source_row_id, event_timestamp, instruction_id FROM direct_debit_events WHERE c.record_table = 'direct_debit_events' AND id = c.record_id
            UNION ALL
            SELECT delivery_id, source_row_id, event_timestamp, card_transaction_id FROM card_events WHERE c.record_table = 'card_events' AND id = c.record_id
            UNION ALL
            SELECT delivery_id, ledger_entry_id, booking_timestamp, COALESCE(transaction_reference, ledger_entry_id) FROM ledger_entries WHERE c.record_table = 'ledger_entries' AND id = c.record_id
            UNION ALL
            SELECT delivery_id, external_transaction_id, booking_timestamp, COALESCE(bank_reference, external_transaction_id) FROM bank_transactions WHERE c.record_table = 'bank_transactions' AND id = c.record_id
        ) r ON true
        LEFT JOIN source_deliveries sd ON sd.id = r.delivery_id
        """;

    public async Task<RunScope?> GetRunScopeAsync(string runId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, legal_entity_id, state FROM runs WHERE id = @r");
        cmd.Parameters.AddWithValue("r", runId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new RunScope(rd.GetString(0), rd.GetString(1), rd.GetString(2)) : null;
    }

    public async Task<long> CountGroupsAsync(string runId, string? status, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT count(*) FROM match_groups WHERE run_id = @r AND (@s::text IS NULL OR status = @s)");
        cmd.Parameters.AddWithValue("r", runId);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<MatchGroupRow>> ListGroupsAsync(string runId, string? status, int limit, string? afterId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(GroupSelect + """
             WHERE g.run_id = @r AND (@s::text IS NULL OR g.status = @s) AND (@after::text IS NULL OR g.id > @after)
             ORDER BY g.id LIMIT @lim
            """);
        cmd.Parameters.AddWithValue("r", runId);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Text) { Value = (object?)afterId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("lim", limit + 1);
        return await ReadGroupsAsync(cmd, ct);
    }

    public async Task<MatchGroupRow?> GetGroupAsync(string runId, string groupId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(GroupSelect + " WHERE g.run_id = @r AND g.id = @g");
        cmd.Parameters.AddWithValue("r", runId);
        cmd.Parameters.AddWithValue("g", groupId);
        return (await ReadGroupsAsync(cmd, ct)).FirstOrDefault();
    }

    private const string GroupSelect = """
        SELECT g.id, g.status, g.manual, g.difference_amount, g.currency, g.rule_id, g.version, cr.config_version,
               g.tolerance, g.rationale, g.cardinality,
               a.id, a.object_type, a.object_id, a.run_id, a.proposal_version, a.proposer_user_id, pu.name, a.approver_user_id, au.name,
               a.required_role, a.status, a.decision, a.rationale, a.evidence_refs::text, a.policy_version, a.proposed_at, a.decided_at
        FROM match_groups g
        LEFT JOIN config_rules cr ON cr.id = g.rule_id
        LEFT JOIN LATERAL (SELECT * FROM approvals x WHERE x.object_type = 'MANUAL_MATCH' AND x.object_id = g.id ORDER BY x.proposed_at DESC, x.id DESC LIMIT 1) a ON true
        LEFT JOIN users pu ON pu.id = a.proposer_user_id
        LEFT JOIN users au ON au.id = a.approver_user_id
        """;

    private async Task<IReadOnlyList<MatchGroupRow>> ReadGroupsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var heads = new List<(int Version, string Id, string Status, bool Manual, MatchMoney? Diff, string? Rule, string? Tol, string? Rationale, string? Cardinality, MatchApprovalRow? Approval)>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetString(0);
                var manual = rd.GetBoolean(2);
                MatchMoney? diff = rd.IsDBNull(3) ? null : new MatchMoney(rd.GetDecimal(3), rd.GetString(4).Trim());
                // ruleVersion: "<ruleId>@<configVersion>"; the bare rule id when its config row is gone; null for a manual match without a rule.
                var rule = rd.IsDBNull(5) ? null : rd.IsDBNull(7) ? rd.GetString(5) : rd.GetString(5) + "@" + rd.GetString(7);
                MatchApprovalRow? ap = null;
                if (!rd.IsDBNull(11))
                {
                    ap = new MatchApprovalRow(rd.GetString(11), rd.GetString(12), rd.GetString(13), rd.IsDBNull(14) ? null : rd.GetString(14), rd.GetInt32(15),
                        rd.GetString(16), rd.GetString(17), rd.IsDBNull(18) ? null : rd.GetString(18), rd.IsDBNull(19) ? null : rd.GetString(19),
                        rd.GetString(20), rd.GetString(21), rd.IsDBNull(22) ? null : rd.GetString(22), rd.IsDBNull(23) ? null : rd.GetString(23),
                        ParseRefs(rd.GetString(24)), rd.GetString(25), rd.GetFieldValue<DateTimeOffset>(26),
                        rd.IsDBNull(27) ? null : rd.GetFieldValue<DateTimeOffset>(27));
                }
                heads.Add((rd.GetInt32(6), id, rd.GetString(1), manual, diff, rule, rd.IsDBNull(8) ? null : rd.GetString(8), rd.IsDBNull(9) ? null : rd.GetString(9),
                    rd.IsDBNull(10) ? null : rd.GetString(10), ap));
            }
        }
        if (heads.Count == 0) { return []; }

        var ids = heads.Select(h => h.Id).ToArray();
        var cands = new Dictionary<string, List<(MatchCandidateRow Row, string[] Exceptions)>>();
        await using (var cc = db.CreateCommand(CandidateSelect + " WHERE c.match_group_id = ANY(@ids) ORDER BY c.match_group_id, c.id"))
        {
            cc.Parameters.AddWithValue("ids", ids);
            await using var rd = await cc.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var (gid, row, ex) = ReadCandidate(rd);
                (cands.TryGetValue(gid, out var l) ? l : cands[gid] = []).Add((row, ex));
            }
        }

        // Explicit group -> exception links (match_group_exceptions) in addition to the exceptions derived from the candidates' records.
        var linked = new Dictionary<string, List<string>>();
        await using (var lc = db.CreateCommand("SELECT match_group_id, exception_id FROM match_group_exceptions WHERE match_group_id = ANY(@ids)"))
        {
            lc.Parameters.AddWithValue("ids", ids);
            await using var rd = await lc.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { (linked.TryGetValue(rd.GetString(0), out var l) ? l : linked[rd.GetString(0)] = []).Add(rd.GetString(1)); }
        }

        return heads.Select(h =>
        {
            var list = cands.TryGetValue(h.Id, out var l) ? l : [];
            var left = list.Where(x => x.Row.Side == "LEFT").Select(x => x.Row).ToList();
            var right = list.Where(x => x.Row.Side == "RIGHT").Select(x => x.Row).ToList();
            var ex = list.SelectMany(x => x.Exceptions).Concat(linked.GetValueOrDefault(h.Id) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return new MatchGroupRow(h.Id, h.Status, h.Manual, h.Diff, h.Rule, h.Tol, h.Rationale, h.Cardinality ?? $"{Card(left.Count)}:{Card(right.Count)}",
                left, right, ex, h.Approval, h.Version);
        }).ToList();
    }

    private static string Card(int n) => n <= 1 ? n.ToString() : "N";

    private static IReadOnlyList<string> ParseRefs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList()
            : [];
    }

    private static (string GroupId, MatchCandidateRow Row, string[] Exceptions) ReadCandidate(NpgsqlDataReader rd)
    {
        var kind = rd.GetString(2);
        var table = rd.GetString(3);
        var recId = rd.GetInt64(4);
        var rowKey = rd.IsDBNull(11) ? recId.ToString(System.Globalization.CultureInfo.InvariantCulture) : rd.GetString(11);
        // side is stored; rows written before the column existed were back-filled by the migration.
        var side = rd.IsDBNull(9) ? (kind is "BANK_EVIDENCE" or "LEDGER_ENTRY" ? "RIGHT" : "LEFT") : rd.GetString(9);
        var row = new MatchCandidateRow(TransactionId(table, recId), side, kind, $"{rd.GetString(14)} {rowKey}",
            new MatchMoney(rd.GetDecimal(5), rd.GetString(6).Trim()), rd.IsDBNull(12) ? null : rd.GetFieldValue<DateTimeOffset>(12),
            rd.IsDBNull(13) ? null : rd.GetString(13), rd.GetBoolean(8), rd.IsDBNull(7) ? null : rd.GetDecimal(7), rd.GetString(1), rd.GetInt64(0));
        return (rd.GetString(1), row, rd.GetFieldValue<string[]>(15));
    }

    public async Task<(string? GroupId, string? FocusTransactionId, FocusFailure Failure)> ResolveFocusAsync(
        string runId, string? transactionId, string? exceptionId, CancellationToken ct)
    {
        if (transactionId is not null)
        {
            if (!TransactionIds.TryParse(transactionId, out var table, out var id)) { return (null, null, FocusFailure.InvalidTransactionId); }
            // table comes from the TransactionIds whitelist, never from the caller.
            await using (var exists = db.CreateCommand($"SELECT 1 FROM {table} t JOIN source_deliveries d ON d.id = t.delivery_id AND d.run_id = @r WHERE t.id = @i"))
            {
                exists.Parameters.AddWithValue("r", runId);
                exists.Parameters.AddWithValue("i", id);
                if (await exists.ExecuteScalarAsync(ct) is null) { return (null, null, FocusFailure.NotFound); }
            }
            await using var cmd = db.CreateCommand("""
                SELECT c.match_group_id FROM match_candidates c JOIN match_groups g ON g.id = c.match_group_id
                WHERE g.run_id = @r AND c.record_table = @t AND c.record_id = @i ORDER BY c.match_group_id LIMIT 1
                """);
            cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.AddWithValue("t", table);
            cmd.Parameters.AddWithValue("i", id);
            return (await cmd.ExecuteScalarAsync(ct) as string, transactionId, FocusFailure.None);
        }
        if (exceptionId is not null)
        {
            await using (var exists = db.CreateCommand("SELECT 1 FROM exceptions WHERE id = @e AND run_id = @r"))
            {
                exists.Parameters.AddWithValue("r", runId);
                exists.Parameters.AddWithValue("e", exceptionId);
                if (await exists.ExecuteScalarAsync(ct) is null) { return (null, null, FocusFailure.NotFound); }
            }
            // Candidates tied to the exception: by delivery/row, by explicit exception_links, or via a link to the whole group.
            await using var cmd = db.CreateCommand("""
                SELECT x.match_group_id, x.record_table, x.record_id FROM (
                    SELECT c.match_group_id, c.record_table, c.record_id, c.id AS cid
                    FROM match_candidates c JOIN match_groups g ON g.id = c.match_group_id
                    LEFT JOIN LATERAL (
                        SELECT delivery_id, source_row_id AS row_key FROM transfer_instructions WHERE c.record_table = 'transfer_instructions' AND id = c.record_id
                        UNION ALL SELECT delivery_id, source_row_id FROM direct_debit_events WHERE c.record_table = 'direct_debit_events' AND id = c.record_id
                        UNION ALL SELECT delivery_id, source_row_id FROM card_events WHERE c.record_table = 'card_events' AND id = c.record_id
                        UNION ALL SELECT delivery_id, ledger_entry_id FROM ledger_entries WHERE c.record_table = 'ledger_entries' AND id = c.record_id
                        UNION ALL SELECT delivery_id, external_transaction_id FROM bank_transactions WHERE c.record_table = 'bank_transactions' AND id = c.record_id
                    ) r ON true
                    JOIN exceptions e ON e.id = @e AND e.run_id = @r AND g.run_id = @r AND e.delivery_id = r.delivery_id AND e.source_row_id = r.row_key
                    UNION ALL
                    SELECT c.match_group_id, c.record_table, c.record_id, c.id
                    FROM exception_links l JOIN match_candidates c ON c.record_table = l.record_table AND c.record_id = l.record_id
                    JOIN match_groups g ON g.id = c.match_group_id AND g.run_id = @r
                    WHERE l.exception_id = @e
                    UNION ALL
                    SELECT c.match_group_id, c.record_table, c.record_id, c.id
                    FROM match_group_exceptions mge JOIN match_groups g ON g.id = mge.match_group_id AND g.run_id = @r
                    JOIN match_candidates c ON c.match_group_id = g.id
                    WHERE mge.exception_id = @e) x
                ORDER BY x.match_group_id, x.cid LIMIT 1
                """);
            cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.AddWithValue("e", exceptionId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct) ? (rd.GetString(0), TransactionId(rd.GetString(1), rd.GetInt64(2)), FocusFailure.None) : (null, null, FocusFailure.None);
        }
        await using (var cmd = db.CreateCommand("""
            SELECT g.id, c.record_table, c.record_id FROM match_groups g
            LEFT JOIN LATERAL (SELECT record_table, record_id FROM match_candidates WHERE match_group_id = g.id ORDER BY id LIMIT 1) c ON true
            WHERE g.run_id = @r AND g.status <> 'MATCHED' ORDER BY g.id LIMIT 1
            """))
        {
            cmd.Parameters.AddWithValue("r", runId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) { return (null, null, FocusFailure.None); }
            return (rd.GetString(0), rd.IsDBNull(1) ? null : TransactionId(rd.GetString(1), rd.GetInt64(2)), FocusFailure.None);
        }
    }

    private static void UnmatchedWhere(NpgsqlCommand cmd, System.Text.StringBuilder w, string runId, UnmatchedFilter? f)
    {
        w.Append("g.run_id = @r AND g.status = 'UNMATCHED'");
        cmd.Parameters.AddWithValue("r", runId);
        if (f?.Pair is { Length: > 0 } pair) { w.Append(" AND c.kind = @pair"); cmd.Parameters.AddWithValue("pair", pair); }
        if (f?.Currency is { Length: > 0 } cur) { w.Append(" AND c.currency = @cur"); cmd.Parameters.AddWithValue("cur", cur); }
        if (f?.Search is { } s && !string.IsNullOrWhiteSpace(s))
        {
            w.Append(" AND (r.ref ILIKE @q OR r.row_key ILIKE @q OR COALESCE(sd.source_name, c.record_table) ILIKE @q");
            cmd.Parameters.AddWithValue("q", "%" + s.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            if (TransactionIds.TryParse(s.Trim(), out var table, out var id))
            {
                w.Append(" OR (c.record_table = @st AND c.record_id = @sid)");
                cmd.Parameters.AddWithValue("st", table);
                cmd.Parameters.AddWithValue("sid", id);
            }
            w.Append(')');
        }
    }

    public async Task<IReadOnlyList<MatchCandidateRow>> ListUnmatchedAsync(string runId, int limit, CancellationToken ct, UnmatchedFilter? filter = null, UnmatchedCursorState? after = null)
    {
        await using var cmd = db.CreateCommand();
        var w = new System.Text.StringBuilder();
        UnmatchedWhere(cmd, w, runId, filter);
        if (after is not null)
        {
            w.Append(" AND (c.match_group_id > @ag OR (c.match_group_id = @ag AND c.id > @ac))");
            cmd.Parameters.AddWithValue("ag", after.GroupId);
            cmd.Parameters.AddWithValue("ac", after.CandidateId);
        }
        cmd.CommandText = $"{CandidateSelect} WHERE {w} ORDER BY c.match_group_id, c.id LIMIT @lim";
        cmd.Parameters.AddWithValue("lim", limit);
        var list = new List<MatchCandidateRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) { list.Add(ReadCandidate(rd).Row); }
        return list;
    }

    public async Task<long> CountUnmatchedAsync(string runId, UnmatchedFilter? filter, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        var w = new System.Text.StringBuilder();
        UnmatchedWhere(cmd, w, runId, filter);
        // Same joins as the list so the filter and the total cannot diverge.
        cmd.CommandText = $"SELECT count(*) FROM ({CandidateSelect} WHERE {w}) x";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<MatchingStats> GetStatsAsync(string runId, CancellationToken ct)
    {
        long total, open, auto;
        await using (var cmd = db.CreateCommand("""
            SELECT count(*), count(*) FILTER (WHERE status <> 'MATCHED'), count(*) FILTER (WHERE status = 'MATCHED' AND NOT manual)
            FROM match_groups WHERE run_id = @r
            """))
        {
            cmd.Parameters.AddWithValue("r", runId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            total = rd.GetInt64(0); open = rd.GetInt64(1); auto = rd.GetInt64(2);
        }
        var byCcy = new List<CurrencyUnmatched>();
        await using (var cmd = db.CreateCommand("""
            SELECT c.currency, count(*), COALESCE(sum(c.amount), 0) FROM match_candidates c JOIN match_groups g ON g.id = c.match_group_id
            WHERE g.run_id = @r AND g.status = 'UNMATCHED' GROUP BY c.currency ORDER BY c.currency
            """))
        {
            cmd.Parameters.AddWithValue("r", runId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { byCcy.Add(new CurrencyUnmatched(rd.GetString(0).Trim(), rd.GetInt64(1), rd.GetDecimal(2))); }
        }
        return new MatchingStats(total == 0 ? null : decimal.Round((decimal)auto / total, 4), total, open, byCcy.Sum(c => c.Count), byCcy);
    }
}
