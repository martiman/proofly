using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Safeguarding;

public interface ISafeguardingReadRepository
{
    /// <summary>The run header when it exists AND its legal entity is in scope; null otherwise (caller answers 404).</summary>
    Task<SafeguardingRun?> GetRunAsync(string runId, EntityScope scope, CancellationToken ct);

    /// <summary>Full position view of an in-scope run; null when the run is not found or out of scope.</summary>
    Task<SafeguardingView?> GetPositionAsync(string runId, EntityScope scope, CancellationToken ct);

    /// <summary>As above, with at most <paramref name="linesLimit"/> obligation lines and balance lines per currency (largest absolute amount first).</summary>
    Task<SafeguardingView?> GetPositionAsync(string runId, EntityScope scope, int linesLimit, CancellationToken ct);

    /// <summary>
    /// One keyset page of the obligation (<c>obligation</c>) or balance (<c>balance</c>) lines of one currency, largest absolute amount first.
    /// Null when the run is not found or out of scope, or the currency has no position in the run.
    /// </summary>
    Task<LinePageRows?> ListLinesAsync(string runId, string currency, string kind, string? search, int limit, LineKey? after, bool includeTotal, EntityScope scope, CancellationToken ct);

    /// <summary>All adjustments of an in-scope run (optionally one status), oldest first; null when run not found or out of scope.</summary>
    Task<IReadOnlyList<AdjustmentRow>?> ListAdjustmentsAsync(string runId, string? status, EntityScope scope, CancellationToken ct);

    /// <summary>One keyset page of <see cref="ListAdjustmentsAsync"/> (limit + 1 rows fetched to detect another page); null when run not found or out of scope.</summary>
    Task<AdjustmentPageRows?> ListAdjustmentsPageAsync(string runId, string? status, int limit, AdjustmentCursor? after, bool includeTotal, EntityScope scope, CancellationToken ct);
}

/// <summary>
/// Reads persisted safeguarding output. <c>safeguarding_positions.detail</c> carries the control's line model (mirrors
/// Proofly.Controls CurrencyPosition): <c>approvedAdjustments</c> (decimal string), <c>qualifications</c> (string[]),
/// <c>obligationLines</c> [{accountId, amount, movementType}], <c>balanceLines</c> [{sourceRowId, included, reason, findingIds[]}].
/// Balance facts (amount, account, timestamp, evidence) are joined from <c>safeguarding_balances</c>.
/// </summary>
public sealed class SafeguardingReadRepository(NpgsqlDataSource db) : ISafeguardingReadRepository
{
    private const string ScopePredicate = "(@all OR r.legal_entity_id = ANY(@ids))";

    private static void AddScope(NpgsqlCommand c, EntityScope scope)
    {
        c.Parameters.AddWithValue("all", scope.All);
        c.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = scope.Ids.ToArray() });
    }

    public async Task<SafeguardingRun?> GetRunAsync(string runId, EntityScope scope, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"SELECT r.id, r.legal_entity_id, r.business_date, r.sign_off_state FROM runs r WHERE r.id=@id AND {ScopePredicate}");
        c.Parameters.AddWithValue("id", runId);
        AddScope(c, scope);
        await using var rd = await c.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new SafeguardingRun(rd.GetString(0), rd.GetString(1), DateOnly.FromDateTime(rd.GetDateTime(2)), rd.GetString(3))
            : null;
    }

    public const int DefaultLinesLimit = 50;

    public Task<SafeguardingView?> GetPositionAsync(string runId, EntityScope scope, CancellationToken ct) => GetPositionAsync(runId, scope, DefaultLinesLimit, ct);

    /// <summary>Array of the position detail, or an empty array when absent or not an array. Lines stay in the database; only the requested page is read.</summary>
    private static string Arr(string name) => $"(CASE WHEN jsonb_typeof(p.detail->'{name}')='array' THEN p.detail->'{name}' ELSE '[]'::jsonb END)";

    /// <summary>Amount of a detail line element as NUMERIC; 0 when it is not a decimal (the control always writes decimal strings).</summary>
    private const string LineAmount = "(CASE WHEN (t.e->>'amount') ~ '^-?[0-9]+(\\.[0-9]+)?$' THEN (t.e->>'amount')::numeric ELSE 0 END)";

    private static string Like(string search) => "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public async Task<SafeguardingView?> GetPositionAsync(string runId, EntityScope scope, int linesLimit, CancellationToken ct)
    {
        var run = await GetRunAsync(runId, scope, ct);
        if (run is null) { return null; }

        // Positions + approved adjustments (live fallback when the snapshot carries none). All sums stay NUMERIC in SQL.
        var positions = new List<PositionRow>();
        var obligations = new List<ObligationLineRow>();
        var obligationTotals = new Dictionary<string, int>();
        var exceptions = await ExceptionIdsAsync(run, ct);
        var liveApproved = await ApprovedByCurrencyAsync(run, ct);

        await using (var c = db.CreateCommand(
            $@"SELECT p.currency, p.obligation_amount, p.eligible_amount, p.difference_amount, p.state, p.stale, p.detail->>'approvedAdjustments',
                      {Arr("qualifications")}::text, jsonb_array_length({Arr("obligationLines")})
               FROM safeguarding_positions p WHERE p.run_id=@r AND p.legal_entity_id=@le ORDER BY p.currency"))
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var ccy = rd.GetString(0).Trim();
                using var q = JsonDocument.Parse(rd.GetString(7));
                var approved = !rd.IsDBNull(6) && decimal.TryParse(rd.GetString(6), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ap)
                    ? ap : liveApproved.GetValueOrDefault(ccy);
                positions.Add(new PositionRow(ccy, rd.GetDecimal(1), rd.GetDecimal(2), approved, rd.IsDBNull(3) ? null : rd.GetDecimal(3), rd.GetString(4),
                    Strings(q.RootElement, null), exceptions.GetValueOrDefault(ccy) ?? [], rd.GetBoolean(5)));
                obligationTotals[ccy] = rd.GetInt32(8);
            }
        }

        await using (var c = db.CreateCommand(
            $@"SELECT p.currency, t.e->>'accountId', t.e->>'amount', t.e->>'movementType'
               FROM safeguarding_positions p
               CROSS JOIN LATERAL (
                   SELECT t.e, t.ord, {LineAmount} AS amt FROM jsonb_array_elements({Arr("obligationLines")}) WITH ORDINALITY AS t(e, ord)
                   ORDER BY abs({LineAmount}) DESC, t.ord LIMIT @n) t
               WHERE p.run_id=@r AND p.legal_entity_id=@le
               ORDER BY p.currency, abs(t.amt) DESC, t.ord"))
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            c.Parameters.AddWithValue("n", linesLimit);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                obligations.Add(new ObligationLineRow(rd.GetString(1), rd.GetString(0).Trim(),
                    rd.IsDBNull(2) ? 0m : decimal.Parse(rd.GetString(2), CultureInfo.InvariantCulture), rd.IsDBNull(3) ? null : rd.GetString(3)));
            }
        }

        var balances = new List<BalanceLineRow>();
        var balanceTotals = new Dictionary<string, int>();
        await using (var c = db.CreateCommand(
            @"SELECT s.id, s.source_row_id, s.external_account_id, s.balance_timestamp, s.balance_amount, s.currency, s.evidence_reference, s.total
              FROM (SELECT b.id, b.source_row_id, b.external_account_id, b.balance_timestamp, b.balance_amount, b.currency, b.evidence_reference,
                           row_number() OVER (PARTITION BY b.currency ORDER BY abs(b.balance_amount) DESC, b.id) AS rn,
                           count(*) OVER (PARTITION BY b.currency) AS total
                    FROM safeguarding_balances b JOIN source_deliveries d ON d.id=b.delivery_id
                    WHERE d.run_id=@r AND d.state <> 'SUPERSEDED' AND b.legal_entity_id=@le) s
              WHERE s.rn <= @n ORDER BY s.currency, s.rn"))
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            c.Parameters.AddWithValue("n", (long)linesLimit);
            var raw = new List<BalanceLineRow>();
            await using (var rd = await c.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    raw.Add(ReadBalance(rd));
                    balanceTotals[rd.GetString(5).Trim()] = (int)rd.GetInt64(7);
                }
            }
            balances.AddRange(await WithLineDetailAsync(run, raw, ct));
        }

        var previous = new List<PreviousPosition>();
        await using (var c = db.CreateCommand(
            @"SELECT p.currency, p.obligation_amount, p.eligible_amount, p.difference_amount, p.detail::text
              FROM safeguarding_positions p
              WHERE p.run_id = (SELECT id FROM runs WHERE legal_entity_id=@le AND business_date < @bd ORDER BY business_date DESC, run_version DESC LIMIT 1)
              ORDER BY p.currency"))
        {
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            c.Parameters.AddWithValue("bd", NpgsqlDbType.Date, run.BusinessDate);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                using var doc = JsonDocument.Parse(rd.GetString(4));
                previous.Add(new PreviousPosition(rd.GetString(0).Trim(), rd.GetDecimal(1), rd.GetDecimal(2),
                    TryDecimal(doc.RootElement, "approvedAdjustments") ?? 0m, rd.IsDBNull(3) ? null : rd.GetDecimal(3)));
            }
        }

        var adjustments = await ListAdjustmentsAsync(run.RunId, null, scope, ct) ?? [];
        return new SafeguardingView(run, await ReadinessAsync(run, positions, ct), positions, obligations, balances, adjustments, previous, obligationTotals, balanceTotals);
    }

    private static BalanceLineRow ReadBalance(NpgsqlDataReader rd) => new(rd.GetString(1), "safeguarding_balances:" + rd.GetInt64(0).ToString(CultureInfo.InvariantCulture),
        rd.GetString(2), new DateTimeOffset(rd.GetDateTime(3), TimeSpan.Zero), rd.GetDecimal(4), rd.GetString(5).Trim(), rd.IsDBNull(6) ? null : rd.GetString(6), false, "NOT_EVALUATED", []);

    /// <summary>Adds the control's per-line verdict (included, reason, findings) from the position detail; lines the control did not evaluate stay NOT_EVALUATED.</summary>
    private async Task<IReadOnlyList<BalanceLineRow>> WithLineDetailAsync(SafeguardingRun run, IReadOnlyList<BalanceLineRow> lines, CancellationToken ct)
    {
        if (lines.Count == 0) { return lines; }
        var detail = new Dictionary<(string Currency, string Row), (bool Included, string Reason, string[] Findings)>();
        await using var c = db.CreateCommand(
            $@"SELECT p.currency, t.e->>'sourceRowId', t.e->>'included', t.e->>'reason', COALESCE(t.e->'findingIds', '[]'::jsonb)::text
               FROM safeguarding_positions p CROSS JOIN LATERAL jsonb_array_elements({Arr("balanceLines")}) AS t(e)
               WHERE p.run_id=@r AND p.legal_entity_id=@le AND t.e->>'sourceRowId' = ANY(@rows)");
        c.Parameters.AddWithValue("r", run.RunId);
        c.Parameters.AddWithValue("le", run.LegalEntityId);
        c.Parameters.Add(new NpgsqlParameter("rows", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = lines.Select(l => l.SourceRowId).Distinct().ToArray() });
        await using (var rd = await c.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                using var f = JsonDocument.Parse(rd.GetString(4));
                detail[(rd.GetString(0).Trim(), rd.GetString(1))] = (rd.IsDBNull(2) ? false : rd.GetString(2) == "true", rd.IsDBNull(3) ? "" : rd.GetString(3), Strings(f.RootElement, null).ToArray());
            }
        }
        return lines.Select(l => detail.TryGetValue((l.Currency, l.SourceRowId), out var d) ? l with { Included = d.Included, Reason = d.Reason, FindingIds = d.Findings } : l).ToList();
    }

    public async Task<LinePageRows?> ListLinesAsync(string runId, string currency, string kind, string? search, int limit, LineKey? after, bool includeTotal, EntityScope scope, CancellationToken ct)
    {
        var run = await GetRunAsync(runId, scope, ct);
        if (run is null) { return null; }
        await using (var c = db.CreateCommand("SELECT 1 FROM safeguarding_positions WHERE run_id=@r AND legal_entity_id=@le AND currency::text=@ccy"))
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            c.Parameters.AddWithValue("ccy", currency);
            if (await c.ExecuteScalarAsync(ct) is null) { return null; }
        }

        var like = string.IsNullOrEmpty(search) ? null : Like(search);
        void Bind(NpgsqlCommand c, bool cursor)
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            c.Parameters.AddWithValue("ccy", currency);
            c.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)like ?? DBNull.Value });
            if (!cursor) { return; }
            c.Parameters.AddWithValue("hasCur", after is not null);
            c.Parameters.Add(new NpgsqlParameter("ca", NpgsqlDbType.Numeric) { Value = after?.NegAbs ?? 0m });
            c.Parameters.Add(new NpgsqlParameter("ct", NpgsqlDbType.Bigint) { Value = after?.Tie ?? 0L });
            c.Parameters.AddWithValue("lim", limit + 1);
        }

        if (kind == "obligation")
        {
            const string from = @"FROM safeguarding_positions p
                CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(p.detail->'obligationLines')='array' THEN p.detail->'obligationLines' ELSE '[]'::jsonb END) WITH ORDINALITY AS t(e, ord)
                WHERE p.run_id=@r AND p.legal_entity_id=@le AND p.currency::text=@ccy AND (@s::text IS NULL OR (t.e->>'accountId') ILIKE @s)";
            var key = "-abs(" + LineAmount + ")";
            var items = new List<ObligationLineItem>();
            await using (var c = db.CreateCommand(
                $@"SELECT t.e->>'accountId', t.e->>'amount', t.e->>'movementType', {key}, t.ord {from}
                   AND (@hasCur = false OR ({key}, t.ord) > (@ca, @ct)) ORDER BY {key}, t.ord LIMIT @lim"))
            {
                Bind(c, true);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    items.Add(new ObligationLineItem(new ObligationLineRow(rd.GetString(0), currency, rd.IsDBNull(1) ? 0m : decimal.Parse(rd.GetString(1), CultureInfo.InvariantCulture),
                        rd.IsDBNull(2) ? null : rd.GetString(2)), new LineKey(rd.GetDecimal(3), rd.GetInt64(4))));
                }
            }
            int? total = null;
            if (includeTotal)
            {
                await using var c = db.CreateCommand("SELECT count(*) " + from);
                Bind(c, false);
                total = (int)(long)(await c.ExecuteScalarAsync(ct))!;
            }
            return new LinePageRows(items, [], total);
        }

        const string bFrom = @"FROM safeguarding_balances b JOIN source_deliveries d ON d.id=b.delivery_id
            WHERE d.run_id=@r AND d.state <> 'SUPERSEDED' AND b.legal_entity_id=@le AND b.currency::text=@ccy AND (@s::text IS NULL OR b.external_account_id ILIKE @s)";
        var raw = new List<(BalanceLineRow Row, LineKey Key)>();
        await using (var c = db.CreateCommand(
            $@"SELECT b.id, b.source_row_id, b.external_account_id, b.balance_timestamp, b.balance_amount, b.currency, b.evidence_reference, -abs(b.balance_amount) {bFrom}
               AND (@hasCur = false OR (-abs(b.balance_amount), b.id) > (@ca, @ct)) ORDER BY -abs(b.balance_amount), b.id LIMIT @lim"))
        {
            Bind(c, true);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { raw.Add((ReadBalance(rd), new LineKey(rd.GetDecimal(7), rd.GetInt64(0)))); }
        }
        var detailed = await WithLineDetailAsync(run, raw.Select(r => r.Row).ToList(), ct);
        var balances = detailed.Select((l, i) => new BalanceLineItem(l, raw[i].Key)).ToList();
        int? bTotal = null;
        if (includeTotal)
        {
            await using var c = db.CreateCommand("SELECT count(*) " + bFrom);
            Bind(c, false);
            bTotal = (int)(long)(await c.ExecuteScalarAsync(ct))!;
        }
        return new LinePageRows([], balances, bTotal);
    }

    public async Task<IReadOnlyList<AdjustmentRow>?> ListAdjustmentsAsync(string runId, string? status, EntityScope scope, CancellationToken ct) =>
        await LoadAdjustmentsAsync(runId, status, null, null, scope, ct);

    public async Task<AdjustmentPageRows?> ListAdjustmentsPageAsync(
        string runId, string? status, int limit, AdjustmentCursor? after, bool includeTotal, EntityScope scope, CancellationToken ct)
    {
        var loaded = await LoadAdjustmentsAsync(runId, status, limit + 1, after, scope, ct);
        if (loaded is null) { return null; }
        var rows = loaded;
        var more = rows.Count > limit;
        int? total = null;
        if (includeTotal)
        {
            await using var c = db.CreateCommand(
                $"SELECT count(*) FROM safeguarding_adjustments a JOIN runs r ON r.id=a.run_id WHERE a.run_id=@r AND (@status::text IS NULL OR a.status=@status) AND {ScopePredicate}");
            c.Parameters.AddWithValue("r", runId);
            c.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
            AddScope(c, scope);
            total = (int)(long)(await c.ExecuteScalarAsync(ct))!;
        }
        return new AdjustmentPageRows(more ? rows.Take(limit).ToList() : rows, more, total);
    }

    private async Task<IReadOnlyList<AdjustmentRow>?> LoadAdjustmentsAsync(
        string runId, string? status, int? limit, AdjustmentCursor? after, EntityScope scope, CancellationToken ct)
    {
        if (await GetRunAsync(runId, scope, ct) is null) { return null; }

        var rows = new List<(string Id, string RunId, string Ccy, decimal Amount, string Status, UserRefRow Owner, string Reason, IReadOnlyList<string> Refs, DateOnly? Exp, int Version, DateTimeOffset At)>();
        await using (var c = db.CreateCommand(
            $@"SELECT a.id, a.run_id, a.currency, a.amount, a.status, a.proposed_by_user_id, u.name, a.reason, a.evidence_refs::text, a.expires_on, a.version, a.created_at
               FROM safeguarding_adjustments a JOIN runs r ON r.id=a.run_id JOIN users u ON u.id=a.proposed_by_user_id
               WHERE a.run_id=@r AND (@status::text IS NULL OR a.status=@status) AND {ScopePredicate}
                 AND (@cat::timestamptz IS NULL OR (a.created_at, a.id) > (@cat, @cid))
               ORDER BY a.created_at, a.id LIMIT @lim"))
        {
            c.Parameters.AddWithValue("r", runId);
            c.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
            c.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.At : DBNull.Value });
            c.Parameters.Add(new NpgsqlParameter("cid", NpgsqlDbType.Text) { Value = (object?)after?.Id ?? "" });
            c.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = (object?)limit ?? int.MaxValue });
            AddScope(c, scope);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                using var refs = JsonDocument.Parse(rd.GetString(8));
                rows.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2).Trim(), rd.GetDecimal(3), rd.GetString(4),
                    new UserRefRow(rd.GetString(5), rd.GetString(6)), rd.GetString(7),
                    refs.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList(),
                    rd.IsDBNull(9) ? null : DateOnly.FromDateTime(rd.GetDateTime(9)), rd.GetInt32(10), rd.GetFieldValue<DateTimeOffset>(11)));
            }
        }
        if (rows.Count == 0) { return []; }
        var ids = rows.Select(r => r.Id).ToArray();

        var approvals = new Dictionary<string, ApprovalRow>();
        await using (var c = db.CreateCommand(
            @"SELECT a.id, a.object_id, a.run_id, a.proposal_version, a.proposer_user_id, p.name, a.approver_user_id, ap.name,
                     a.required_role, a.status, a.decision, a.rationale, a.evidence_refs::text, a.policy_version, a.proposed_at, a.decided_at
              FROM approvals a JOIN users p ON p.id=a.proposer_user_id LEFT JOIN users ap ON ap.id=a.approver_user_id
              WHERE a.object_type='SAFEGUARDING_ADJUSTMENT' AND a.object_id = ANY(@ids)
              ORDER BY a.proposed_at DESC, a.id DESC"))
        {
            c.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids });
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var objectId = rd.GetString(1);
                if (approvals.ContainsKey(objectId)) { continue; } // newest approval record wins
                using var ev = JsonDocument.Parse(rd.GetString(12));
                approvals[objectId] = new ApprovalRow(rd.GetString(0), "SAFEGUARDING_ADJUSTMENT", objectId, rd.IsDBNull(2) ? null : rd.GetString(2),
                    rd.GetInt32(3), new UserRefRow(rd.GetString(4), rd.GetString(5)),
                    rd.IsDBNull(6) ? null : new UserRefRow(rd.GetString(6), rd.GetString(7)),
                    rd.GetString(8), rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetString(11),
                    ev.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList(), rd.GetString(13),
                    rd.GetFieldValue<DateTimeOffset>(14), rd.IsDBNull(15) ? null : rd.GetFieldValue<DateTimeOffset>(15));
            }
        }

        var history = new Dictionary<string, List<HistoryRow>>();
        await using (var c = db.CreateCommand(
            @"SELECT e.id, e.subject_id, e.occurred_at, e.actor_type, e.actor_id, COALESCE(u.name, NULLIF(e.actor_name, '')), e.event_type, COALESCE(e.reason, e.event_type)
              FROM audit_events e LEFT JOIN users u ON u.id=e.actor_id
              WHERE e.subject_kind='ADJUSTMENT' AND e.subject_id = ANY(@ids)
              ORDER BY e.occurred_at, e.id"))
        {
            c.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids });
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var sid = rd.GetString(1);
                if (!history.TryGetValue(sid, out var list)) { history[sid] = list = []; }
                list.Add(new HistoryRow(rd.GetInt64(0).ToString(CultureInfo.InvariantCulture), rd.GetFieldValue<DateTimeOffset>(2),
                    rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5), rd.GetString(6), rd.GetString(7)));
            }
        }

        return rows.Select(r => new AdjustmentRow(r.Id, r.RunId, r.Ccy, r.Amount, r.Status, r.Owner, r.Reason, r.Refs, r.Exp, r.Version,
            approvals.GetValueOrDefault(r.Id), history.GetValueOrDefault(r.Id) ?? [], r.At)).ToList();
    }

    private async Task<Dictionary<string, decimal>> ApprovedByCurrencyAsync(SafeguardingRun run, CancellationToken ct)
    {
        var map = new Dictionary<string, decimal>();
        await using var c = db.CreateCommand(
            @"SELECT currency, SUM(amount) FROM safeguarding_adjustments
              WHERE run_id=@r AND status='APPROVED' AND (expires_on IS NULL OR expires_on >= @bd)
              GROUP BY currency");
        c.Parameters.AddWithValue("r", run.RunId);
        c.Parameters.AddWithValue("bd", NpgsqlDbType.Date, run.BusinessDate);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) { map[rd.GetString(0).Trim()] = rd.GetDecimal(1); }
        return map;
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> ExceptionIdsAsync(SafeguardingRun run, CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>();
        await using var c = db.CreateCommand(
            "SELECT value_currency, id FROM exceptions WHERE run_id=@r AND legal_entity_id=@le AND lower(control)='safeguarding' AND value_currency IS NOT NULL ORDER BY id");
        c.Parameters.AddWithValue("r", run.RunId);
        c.Parameters.AddWithValue("le", run.LegalEntityId);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var ccy = rd.GetString(0).Trim();
            if (!map.TryGetValue(ccy, out var l)) { map[ccy] = l = []; }
            l.Add(rd.GetString(1));
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>INCOMPLETE: no position yet or a delivery is MISSING/REJECTED; QUALIFIED: qualified/late delivery, QUALIFIED state or qualifications; else COMPLETE.</summary>
    private async Task<string> ReadinessAsync(SafeguardingRun run, IReadOnlyList<PositionRow> positions, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT state FROM source_deliveries WHERE run_id=@r AND state <> 'SUPERSEDED'");
        c.Parameters.AddWithValue("r", run.RunId);
        var states = new List<string>();
        await using (var rd = await c.ExecuteReaderAsync(ct)) { while (await rd.ReadAsync(ct)) { states.Add(rd.GetString(0)); } }
        if (positions.Count == 0 || states.Any(s => s is "MISSING" or "REJECTED")) { return "INCOMPLETE"; }
        if (states.Any(s => s is "QUALIFIED" or "LATE") || positions.Any(p => p.State == "QUALIFIED" || p.Qualifications.Count > 0)) { return "QUALIFIED"; }
        return "COMPLETE";
    }

    private static decimal? TryDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && decimal.TryParse(v.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static List<string> Strings(JsonElement e, string? name)
    {
        var v = name is null ? e : e.TryGetProperty(name, out var p) ? p : default;
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
    }
}
