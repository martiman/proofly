using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Proofly.Data.Audit;
using Proofly.Domain.Enums;
using Proofly.Domain.Ids;

namespace Proofly.Data.Matching.Commands;

public enum ManualMatchOutcome
{
    Ok, RunNotFound, GroupNotFound, Validation, RecordAlreadyUsed, InvalidState, RunLocked, RunSuperseded, StaleVersion, SeparationOfDuties, StaleProposal,
}

/// <summary>Result of a manual-match command. Pointer/Detail carry the reason for every non-Ok outcome; nothing is repaired.</summary>
public sealed record ManualMatchResult(ManualMatchOutcome Outcome, string? GroupId = null, string? ApprovalId = null, string? Pointer = null, string? Detail = null)
{
    public static ManualMatchResult Fail(ManualMatchOutcome o, string detail, string? pointer = null) => new(o, Detail: detail, Pointer: pointer);
}

public sealed record ProposeManualMatchCommand(
    string RunId, string UserId, string UserName, IReadOnlyList<string> LeftIds, IReadOnlyList<string> RightIds, string Reason,
    IReadOnlyList<string> EvidenceRefs, string? RequestId);

public sealed record DecideManualMatchCommand(
    string RunId, string GroupId, string UserId, string UserName, bool Approve, string Reason, int? ProposalVersion, int ExpectedGroupVersion, string? RequestId);

/// <summary>
/// Manual match workflow (ADR 0005: every manual match needs a second person). Each method is one transaction: state checks, the change and the
/// audit event commit together; a failed command leaves no row and no audit event.
/// </summary>
public interface IManualMatchCommands
{
    Task<ManualMatchResult> ProposeAsync(ProposeManualMatchCommand cmd, CancellationToken ct);
    Task<ManualMatchResult> DecideAsync(DecideManualMatchCommand cmd, CancellationToken ct);
    Task<MatchApprovalRow?> GetApprovalAsync(string approvalId, CancellationToken ct);
}

public sealed class ManualMatchCommands(NpgsqlDataSource db, IAuditWriter audit) : IManualMatchCommands
{
    public const string PolicyVersion = "adr-0005/1";

    private static readonly Dictionary<string, string> LeftKinds = new()
    {
        ["transfer_instructions"] = "BANK_TRANSFER", ["direct_debit_events"] = "DIRECT_DEBIT", ["card_events"] = "CARD",
    };
    private static readonly Dictionary<string, string> RightKinds = new()
    {
        ["bank_transactions"] = "BANK_EVIDENCE", ["ledger_entries"] = "LEDGER_ENTRY",
    };

    private sealed record Rec(string Id, string Table, long Pk, string Side, string Kind, decimal Amount, string Currency);

    public async Task<ManualMatchResult> ProposeAsync(ProposeManualMatchCommand cmd, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Lock(conn, tx, cmd.RunId, ct);

        string le, runState;
        await using (var c = new NpgsqlCommand("SELECT legal_entity_id, state FROM runs WHERE id = @r", conn, tx))
        {
            c.Parameters.AddWithValue("r", cmd.RunId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) { return ManualMatchResult.Fail(ManualMatchOutcome.RunNotFound, "Run not found."); }
            le = rd.GetString(0); runState = rd.GetString(1);
        }
        if (runState is "SUPERSEDED" or "SIGNED_OFF") { return ManualMatchResult.Fail(runState == "SUPERSEDED" ? ManualMatchOutcome.RunSuperseded : ManualMatchOutcome.RunLocked, $"The run is {runState} and read-only."); }

        // Parse and validate ids (never repaired: a wrong side, a duplicate or an unknown record rejects the whole proposal).
        var recs = new List<Rec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ids, side, kinds, field) in new[] { (cmd.LeftIds, "LEFT", LeftKinds, "/leftTransactionIds"), (cmd.RightIds, "RIGHT", RightKinds, "/rightTransactionIds") })
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var ptr = $"{field}/{i}";
                if (!TransactionIds.TryParse(ids[i], out var table, out var pk)) { return ManualMatchResult.Fail(ManualMatchOutcome.Validation, "The transaction id is not valid.", ptr); }
                if (!kinds.TryGetValue(table, out var kind))
                {
                    return ManualMatchResult.Fail(ManualMatchOutcome.Validation, side == "LEFT" ? "Left records must be transfers, direct debits or card events." : "Right records must be bank transactions or ledger entries.", ptr);
                }
                if (!seen.Add(ids[i])) { return ManualMatchResult.Fail(ManualMatchOutcome.Validation, "The record is listed more than once.", ptr); }
                // table comes from the whitelist above, never from the caller.
                await using var q = new NpgsqlCommand($"SELECT t.amount, t.currency FROM {table} t JOIN source_deliveries d ON d.id = t.delivery_id WHERE d.run_id = @r AND t.id = @i", conn, tx);
                q.Parameters.AddWithValue("r", cmd.RunId);
                q.Parameters.AddWithValue("i", pk);
                await using var rd = await q.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) { return ManualMatchResult.Fail(ManualMatchOutcome.Validation, "The record does not exist in this run.", ptr); }
                recs.Add(new Rec(ids[i], table, pk, side, kind, rd.GetDecimal(0), rd.GetString(1).Trim()));
            }
        }
        var currency = recs[0].Currency;
        var other = recs.FirstOrDefault(r => r.Currency != currency);
        if (other is not null)
        {
            return ManualMatchResult.Fail(ManualMatchOutcome.Validation, $"All records must have the same currency ({currency}); {other.Id} is {other.Currency}.", other.Side == "LEFT" ? "/leftTransactionIds" : "/rightTransactionIds");
        }

        // Records already used by a matched or pending group cannot be matched again.
        var used = new List<string>();
        await using (var c = new NpgsqlCommand("""
            SELECT c.record_table, c.record_id, g.id, g.status FROM match_candidates c
            JOIN match_groups g ON g.id = c.match_group_id
            JOIN unnest(@t::text[], @i::bigint[]) AS u(t, i) ON u.t = c.record_table AND u.i = c.record_id
            WHERE g.run_id = @r AND g.status IN ('MATCHED','PENDING_APPROVAL') AND c.selected
            ORDER BY g.id, c.record_table, c.record_id
            """, conn, tx))
        {
            c.Parameters.AddWithValue("t", recs.Select(r => r.Table).ToArray());
            c.Parameters.AddWithValue("i", recs.Select(r => r.Pk).ToArray());
            c.Parameters.AddWithValue("r", cmd.RunId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { used.Add($"{TransactionIds.Format(rd.GetString(0), rd.GetInt64(1))} in group {rd.GetString(2)} ({rd.GetString(3)})"); }
        }
        if (used.Count > 0) { return ManualMatchResult.Fail(ManualMatchOutcome.RecordAlreadyUsed, "Records already used: " + string.Join("; ", used) + "."); }

        var left = recs.Where(r => r.Side == "LEFT").ToList();
        var right = recs.Where(r => r.Side == "RIGHT").ToList();
        var diff = left.Sum(r => r.Amount) - right.Sum(r => r.Amount); // recorded, never "fixed": left minus right
        var groupId = "MM-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var approvalId = "AP-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var cardinality = $"{Card(left.Count)}:{Card(right.Count)}";

        await using (var c = new NpgsqlCommand("""
            INSERT INTO match_groups (id, run_id, legal_entity_id, status, difference_amount, currency, manual, rule_id, version, rationale, cardinality)
            VALUES (@g, @r, @le, 'PENDING_APPROVAL', @d, @ccy, true, NULL, 1, @why, @card)
            """, conn, tx))
        {
            c.Parameters.AddWithValue("g", groupId); c.Parameters.AddWithValue("r", cmd.RunId); c.Parameters.AddWithValue("le", le);
            c.Parameters.AddWithValue("d", diff); c.Parameters.AddWithValue("ccy", NpgsqlDbType.Char, currency);
            c.Parameters.AddWithValue("why", cmd.Reason); c.Parameters.AddWithValue("card", cardinality);
            await c.ExecuteNonQueryAsync(ct);
        }
        foreach (var r in recs)
        {
            await using var c = new NpgsqlCommand("""
                INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency, score, selected, side)
                VALUES (@g, @k, @t, @i, @a, @ccy, NULL, true, @s)
                """, conn, tx);
            c.Parameters.AddWithValue("g", groupId); c.Parameters.AddWithValue("k", r.Kind); c.Parameters.AddWithValue("t", r.Table);
            c.Parameters.AddWithValue("i", r.Pk); c.Parameters.AddWithValue("a", r.Amount); c.Parameters.AddWithValue("ccy", NpgsqlDbType.Char, r.Currency);
            c.Parameters.AddWithValue("s", r.Side);
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var c = new NpgsqlCommand("""
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, rationale, evidence_refs, policy_version)
            VALUES (@id, 'MANUAL_MATCH', @g, @r, 1, @u, 'APPROVER', 'PENDING', @why, @ev, @pol)
            """, conn, tx))
        {
            c.Parameters.AddWithValue("id", approvalId); c.Parameters.AddWithValue("g", groupId); c.Parameters.AddWithValue("r", cmd.RunId);
            c.Parameters.AddWithValue("u", cmd.UserId); c.Parameters.AddWithValue("why", cmd.Reason);
            c.Parameters.Add(new NpgsqlParameter("ev", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(cmd.EvidenceRefs) });
            c.Parameters.AddWithValue("pol", PolicyVersion);
            await c.ExecuteNonQueryAsync(ct);
        }

        await audit.AppendAsync(tx, new AuditEventInput(
            "MANUAL_MATCH_PROPOSED", new AuditActor(ActorType.User, cmd.UserId, cmd.UserName), "MATCH_GROUP", groupId,
            $"Manual match proposed: {left.Count} left, {right.Count} right record(s), difference {diff.ToString(CultureInfo.InvariantCulture)} {currency}.",
            Reason: cmd.Reason, RunId: cmd.RunId, RequestId: cmd.RequestId,
            AfterJson: JsonSerializer.Serialize(new
            {
                status = "PENDING_APPROVAL", cardinality, approvalId,
                difference = new { amount = diff.ToString(CultureInfo.InvariantCulture), currency },
                left = left.Select(r => r.Id), right = right.Select(r => r.Id), evidenceRefs = cmd.EvidenceRefs,
            })), ct);
        await tx.CommitAsync(ct);
        return new ManualMatchResult(ManualMatchOutcome.Ok, groupId, approvalId);
    }

    public async Task<ManualMatchResult> DecideAsync(DecideManualMatchCommand cmd, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Lock(conn, tx, cmd.RunId, ct);

        string status, runState;
        int version;
        await using (var c = new NpgsqlCommand("""
            SELECT g.status, g.version, r.state FROM match_groups g JOIN runs r ON r.id = g.run_id WHERE g.id = @g AND g.run_id = @r FOR UPDATE OF g
            """, conn, tx))
        {
            c.Parameters.AddWithValue("g", cmd.GroupId); c.Parameters.AddWithValue("r", cmd.RunId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) { return ManualMatchResult.Fail(ManualMatchOutcome.GroupNotFound, "Match group not found."); }
            status = rd.GetString(0); version = rd.GetInt32(1); runState = rd.GetString(2);
        }
        string? approvalId = null, proposer = null, apStatus = null; int proposalVersion = 0;
        await using (var c = new NpgsqlCommand("""
            SELECT id, proposer_user_id, status, proposal_version FROM approvals WHERE object_type = 'MANUAL_MATCH' AND object_id = @g
            ORDER BY proposed_at DESC, id DESC LIMIT 1 FOR UPDATE
            """, conn, tx))
        {
            c.Parameters.AddWithValue("g", cmd.GroupId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { approvalId = rd.GetString(0); proposer = rd.GetString(1); apStatus = rd.GetString(2); proposalVersion = rd.GetInt32(3); }
        }
        if (proposer == cmd.UserId && apStatus == "PENDING") { return ManualMatchResult.Fail(ManualMatchOutcome.SeparationOfDuties, "The proposer cannot decide their own manual match."); }
        if (version != cmd.ExpectedGroupVersion) { return ManualMatchResult.Fail(ManualMatchOutcome.StaleVersion, "The match group changed since it was read."); }
        if (runState is "SUPERSEDED" or "SIGNED_OFF") { return ManualMatchResult.Fail(runState == "SUPERSEDED" ? ManualMatchOutcome.RunSuperseded : ManualMatchOutcome.RunLocked, $"The run is {runState} and read-only."); }
        if (status != "PENDING_APPROVAL" || approvalId is null || apStatus != "PENDING")
        {
            return ManualMatchResult.Fail(ManualMatchOutcome.InvalidState, $"The match group is {status} with no pending manual-match approval.");
        }
        if (cmd.ProposalVersion is { } pv && pv != proposalVersion) { return ManualMatchResult.Fail(ManualMatchOutcome.StaleProposal, $"The proposal is at version {proposalVersion}, not {pv}."); }

        var cands = new List<(string Table, long Pk, string Side)>();
        await using (var c = new NpgsqlCommand("SELECT record_table, record_id, COALESCE(side, CASE WHEN kind IN ('BANK_EVIDENCE','LEDGER_ENTRY') THEN 'RIGHT' ELSE 'LEFT' END) FROM match_candidates WHERE match_group_id = @g ORDER BY id", conn, tx))
        {
            c.Parameters.AddWithValue("g", cmd.GroupId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { cands.Add((rd.GetString(0), rd.GetInt64(1), rd.GetString(2))); }
        }

        await using (var c = new NpgsqlCommand("""
            UPDATE approvals SET status = @st, decision = @dec, approver_user_id = @u, rationale = @why, decided_at = now(), version = version + 1, updated_at = now()
            WHERE id = @id AND status = 'PENDING'
            """, conn, tx))
        {
            c.Parameters.AddWithValue("st", cmd.Approve ? "APPROVED" : "REJECTED"); c.Parameters.AddWithValue("dec", cmd.Approve ? "APPROVE" : "REJECT");
            c.Parameters.AddWithValue("u", cmd.UserId); c.Parameters.AddWithValue("why", cmd.Reason); c.Parameters.AddWithValue("id", approvalId);
            if (await c.ExecuteNonQueryAsync(ct) != 1) { return ManualMatchResult.Fail(ManualMatchOutcome.InvalidState, "The approval is no longer pending."); }
        }

        var cleared = new List<string>();
        var consumed = new List<string>();
        if (cmd.Approve)
        {
            var left = cands.Count(x => x.Side == "LEFT"); var right = cands.Count(x => x.Side == "RIGHT");
            await using (var c = new NpgsqlCommand("""
                UPDATE match_groups SET status = 'MATCHED', rule_id = 'manual', manual = true, cardinality = @card, version = version + 1, updated_at = now()
                WHERE id = @g AND version = @v
                """, conn, tx))
            {
                c.Parameters.AddWithValue("card", $"{Card(left)}:{Card(right)}"); c.Parameters.AddWithValue("g", cmd.GroupId); c.Parameters.AddWithValue("v", version);
                if (await c.ExecuteNonQueryAsync(ct) != 1) { return ManualMatchResult.Fail(ManualMatchOutcome.StaleVersion, "The match group changed since it was read."); }
            }
            // The records now belong to this group: take them out of the (open) groups they came from, keeping those groups' exception links.
            await using (var c = new NpgsqlCommand("""
                WITH moved AS (
                  DELETE FROM match_candidates c USING match_groups g, match_candidates n
                  WHERE g.id = c.match_group_id AND g.run_id = @r AND g.id <> @g AND g.status IN ('UNMATCHED','VALUE_MISMATCH','NOT_APPLICABLE') AND n.match_group_id = @g
                    AND n.record_table = c.record_table AND n.record_id = c.record_id
                  RETURNING c.match_group_id)
                SELECT DISTINCT match_group_id FROM moved ORDER BY 1
                """, conn, tx))
            {
                c.Parameters.AddWithValue("r", cmd.RunId); c.Parameters.AddWithValue("g", cmd.GroupId);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) { consumed.Add(rd.GetString(0)); }
            }
            if (consumed.Count > 0)
            {
                await using var c = new NpgsqlCommand("""
                    INSERT INTO match_group_exceptions (match_group_id, exception_id)
                    SELECT @g, exception_id FROM match_group_exceptions WHERE match_group_id = ANY(@src) ON CONFLICT DO NOTHING
                    """, conn, tx);
                c.Parameters.AddWithValue("g", cmd.GroupId); c.Parameters.AddWithValue("src", consumed.ToArray());
                await c.ExecuteNonQueryAsync(ct);
                await using var d = new NpgsqlCommand("""
                    DELETE FROM match_group_exceptions WHERE match_group_id IN (SELECT id FROM match_groups g WHERE g.id = ANY(@src) AND NOT EXISTS (SELECT 1 FROM match_candidates m WHERE m.match_group_id = g.id));
                    DELETE FROM match_groups g WHERE g.id = ANY(@src) AND NOT EXISTS (SELECT 1 FROM match_candidates m WHERE m.match_group_id = g.id);
                    """, conn, tx);
                d.Parameters.AddWithValue("src", consumed.ToArray());
                await d.ExecuteNonQueryAsync(ct);
            }
            // Clear the matching exceptions that belong to these records (only now that the match is approved).
            await using (var c = new NpgsqlCommand(ClearSql, conn, tx))
            {
                c.Parameters.AddWithValue("g", cmd.GroupId); c.Parameters.AddWithValue("r", cmd.RunId);
                c.Parameters.AddWithValue("why", $"Cleared by approved manual match {cmd.GroupId}: {cmd.Reason}");
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) { cleared.Add(rd.GetString(0)); }
            }
            foreach (var ex in cleared)
            {
                await using var c = new NpgsqlCommand("""
                    INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary) VALUES (@e, 'MANUAL_MATCH_APPROVED', 'USER', @u, @s)
                    """, conn, tx);
                c.Parameters.AddWithValue("e", ex); c.Parameters.AddWithValue("u", cmd.UserId); c.Parameters.AddWithValue("s", $"Cleared by approved manual match {cmd.GroupId}.");
                await c.ExecuteNonQueryAsync(ct);
            }
        }
        else
        {
            // Reject: the proposal was a copy, so discarding it leaves the records exactly as they were (previous status).
            await using var d = new NpgsqlCommand("""
                DELETE FROM match_candidates WHERE match_group_id = @g;
                DELETE FROM match_group_exceptions WHERE match_group_id = @g;
                DELETE FROM match_groups WHERE id = @g AND version = @v;
                """, conn, tx);
            d.Parameters.AddWithValue("g", cmd.GroupId); d.Parameters.AddWithValue("v", version);
            await d.ExecuteNonQueryAsync(ct);
        }

        await audit.AppendAsync(tx, new AuditEventInput(
            "MANUAL_MATCH_DECIDED", new AuditActor(ActorType.User, cmd.UserId, cmd.UserName), "MATCH_GROUP", cmd.GroupId,
            cmd.Approve ? $"Manual match approved; {cleared.Count} exception(s) cleared." : "Manual match rejected; the records keep their previous groups.",
            Reason: cmd.Reason, RunId: cmd.RunId, RequestId: cmd.RequestId,
            BeforeJson: JsonSerializer.Serialize(new { status = "PENDING_APPROVAL", version, approvalId }),
            AfterJson: JsonSerializer.Serialize(new
            {
                status = cmd.Approve ? "MATCHED" : "DISCARDED", ruleId = cmd.Approve ? "manual" : null, approvalId, decision = cmd.Approve ? "APPROVE" : "REJECT",
                clearedExceptionIds = cleared, consumedGroupIds = consumed,
            })), ct);
        await tx.CommitAsync(ct);
        return new ManualMatchResult(ManualMatchOutcome.Ok, cmd.GroupId, approvalId);
    }

    public async Task<MatchApprovalRow?> GetApprovalAsync(string approvalId, CancellationToken ct)
    {
        await using var c = db.CreateCommand("""
            SELECT a.id, a.object_type, a.object_id, a.run_id, a.proposal_version, a.proposer_user_id, pu.name, a.approver_user_id, au.name,
                   a.required_role, a.status, a.decision, a.rationale, a.evidence_refs::text, a.policy_version, a.proposed_at, a.decided_at
            FROM approvals a JOIN users pu ON pu.id = a.proposer_user_id LEFT JOIN users au ON au.id = a.approver_user_id WHERE a.id = @id
            """);
        c.Parameters.AddWithValue("id", approvalId);
        await using var rd = await c.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) { return null; }
        using var refs = JsonDocument.Parse(rd.GetString(13));
        return new MatchApprovalRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetInt32(4), rd.GetString(5), rd.GetString(6),
            rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetString(8), rd.GetString(9), rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetString(11),
            rd.IsDBNull(12) ? null : rd.GetString(12),
            refs.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList(),
            rd.GetString(14), rd.GetFieldValue<DateTimeOffset>(15), rd.IsDBNull(16) ? null : rd.GetFieldValue<DateTimeOffset>(16));
    }

    // Serialises proposals and decisions of one run: the used-record check and the group change must not interleave.
    private static async Task Lock(NpgsqlConnection conn, NpgsqlTransaction tx, string runId, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx);
        c.Parameters.AddWithValue("k", "manual-match:" + runId);
        await c.ExecuteNonQueryAsync(ct);
    }

    private static string Card(int n) => n <= 1 ? n.ToString(CultureInfo.InvariantCulture) : "N";

    // Matching exceptions tied to the group's records (same linkage the read side uses: delivery + row key, exception_links, group links); open ones only.
    private const string ClearSql = """
        WITH recs AS (SELECT record_table, record_id FROM match_candidates WHERE match_group_id = @g),
        keys AS (
            SELECT t.delivery_id, t.source_row_id AS row_key FROM recs r JOIN transfer_instructions t ON r.record_table = 'transfer_instructions' AND t.id = r.record_id
            UNION ALL SELECT t.delivery_id, t.source_row_id FROM recs r JOIN direct_debit_events t ON r.record_table = 'direct_debit_events' AND t.id = r.record_id
            UNION ALL SELECT t.delivery_id, t.source_row_id FROM recs r JOIN card_events t ON r.record_table = 'card_events' AND t.id = r.record_id
            UNION ALL SELECT t.delivery_id, t.ledger_entry_id FROM recs r JOIN ledger_entries t ON r.record_table = 'ledger_entries' AND t.id = r.record_id
            UNION ALL SELECT t.delivery_id, t.external_transaction_id FROM recs r JOIN bank_transactions t ON r.record_table = 'bank_transactions' AND t.id = r.record_id),
        linked AS (
            SELECT e.id FROM exceptions e JOIN keys k ON e.delivery_id = k.delivery_id AND e.source_row_id = k.row_key WHERE e.run_id = @r
            UNION SELECT l.exception_id FROM exception_links l JOIN recs r ON r.record_table = l.record_table AND r.record_id = l.record_id
            UNION SELECT exception_id FROM match_group_exceptions WHERE match_group_id = @g)
        UPDATE exceptions e SET status = 'RESOLVED', resolution_kind = 'RESOLVED', resolution_reason = @why, version = version + 1, updated_at = now()
        WHERE e.run_id = @r AND e.control = 'matching' AND e.status IN ('OPEN','IN_INVESTIGATION','WAITING_FOR_DATA') AND e.id IN (SELECT id FROM linked)
        RETURNING e.id
        """;
}
