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

    public async Task<SafeguardingView?> GetPositionAsync(string runId, EntityScope scope, CancellationToken ct)
    {
        var run = await GetRunAsync(runId, scope, ct);
        if (run is null) { return null; }

        // Positions + approved adjustments (live fallback when the snapshot carries none). All sums stay NUMERIC in SQL.
        var details = new Dictionary<string, JsonElement>();
        var positions = new List<PositionRow>();
        var obligations = new List<ObligationLineRow>();
        var lineDetail = new Dictionary<(string Currency, string Row), (bool Included, string Reason, string[] Findings)>();
        var exceptions = await ExceptionIdsAsync(run, ct);
        var liveApproved = await ApprovedByCurrencyAsync(run, ct);

        await using (var c = db.CreateCommand(
            "SELECT currency, obligation_amount, eligible_amount, difference_amount, state, detail::text, stale FROM safeguarding_positions WHERE run_id=@r AND legal_entity_id=@le ORDER BY currency"))
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var ccy = rd.GetString(0).Trim();
                using var doc = JsonDocument.Parse(rd.GetString(5));
                var d = doc.RootElement;
                var approved = TryDecimal(d, "approvedAdjustments") ?? liveApproved.GetValueOrDefault(ccy);
                positions.Add(new PositionRow(ccy, rd.GetDecimal(1), rd.GetDecimal(2), approved, rd.IsDBNull(3) ? null : rd.GetDecimal(3), rd.GetString(4),
                    Strings(d, "qualifications"), exceptions.GetValueOrDefault(ccy) ?? [], rd.GetBoolean(6)));
                if (d.TryGetProperty("obligationLines", out var ol) && ol.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in ol.EnumerateArray())
                    {
                        obligations.Add(new ObligationLineRow(l.GetProperty("accountId").GetString()!, ccy,
                            TryDecimal(l, "amount") ?? 0m,
                            l.TryGetProperty("movementType", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : null));
                    }
                }
                if (d.TryGetProperty("balanceLines", out var bl) && bl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in bl.EnumerateArray())
                    {
                        lineDetail[(ccy, l.GetProperty("sourceRowId").GetString()!)] = (
                            l.TryGetProperty("included", out var inc) && inc.ValueKind == JsonValueKind.True,
                            l.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString()! : "",
                            Strings(l, "findingIds").ToArray());
                    }
                }
            }
        }

        var balances = new List<BalanceLineRow>();
        await using (var c = db.CreateCommand(
            @"SELECT b.id, b.source_row_id, b.external_account_id, b.balance_timestamp, b.balance_amount, b.currency, b.evidence_reference
              FROM safeguarding_balances b JOIN source_deliveries d ON d.id=b.delivery_id
              WHERE d.run_id=@r AND d.state <> 'SUPERSEDED' AND b.legal_entity_id=@le
              ORDER BY b.currency, b.external_account_id, b.balance_timestamp, b.source_row_id"))
        {
            c.Parameters.AddWithValue("r", run.RunId);
            c.Parameters.AddWithValue("le", run.LegalEntityId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var ccy = rd.GetString(5).Trim();
                var row = rd.GetString(1);
                var has = lineDetail.TryGetValue((ccy, row), out var ld);
                balances.Add(new BalanceLineRow(row, "safeguarding_balances:" + rd.GetInt64(0).ToString(CultureInfo.InvariantCulture), rd.GetString(2),
                    new DateTimeOffset(rd.GetDateTime(3), TimeSpan.Zero), rd.GetDecimal(4), ccy,
                    rd.IsDBNull(6) ? null : rd.GetString(6),
                    has && ld.Included, has ? ld.Reason : "NOT_EVALUATED", has ? ld.Findings : []));
            }
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
        return new SafeguardingView(run, await ReadinessAsync(run, positions, ct), positions, obligations, balances, adjustments, previous);
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

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
}
