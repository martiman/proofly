using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Data.Audit;
using Proofly.Domain.Enums;

namespace Proofly.Data.Exceptions.Commands;

/// <summary>
/// Resolution proposals, their approval (separation of duties) and reopening. One transaction per command; any non-Ok outcome
/// rolls back, so no change and no audit event. The original control result and finding data are never touched: only the
/// exception's workflow status/resolution fields, the proposal, the approval, the timeline and the audit event change.
/// </summary>
public interface IResolutionCommandRepository
{
    Task<CommandOutcome> ProposeAsync(string exceptionId, int expectedVersion, string kind, string reason, IReadOnlyList<string> evidenceRefs, CommandContext ctx, CancellationToken ct = default);
    /// <summary>Forbidden with Pointer "separation-of-duties" when the actor proposed the resolution.</summary>
    Task<CommandOutcome> DecideAsync(string exceptionId, string proposalId, int expectedVersion, bool approve, string reason, int? proposalVersion, CommandContext ctx, CancellationToken ct = default);
    Task<CommandOutcome> ReopenAsync(string exceptionId, int expectedVersion, string reason, CommandContext ctx, CancellationToken ct = default);
    /// <summary>Sign-off gate: true when any blocking exception of the run is not yet resolved, accepted as a difference or a false positive.</summary>
    Task<bool> HasUnresolvedBlockingExceptionsAsync(string runId, CancellationToken ct = default);
}

public static class ResolutionCommandsDataModule
{
    public static IServiceCollection AddResolutionCommandsData(this IServiceCollection s)
    {
        s.AddSingleton<IResolutionCommandRepository, ResolutionCommandRepository>();
        return s;
    }
}

public sealed class ResolutionCommandRepository(NpgsqlDataSource db, IAuditWriter audit) : IResolutionCommandRepository
{
    private static readonly string[] Terminal = ["RESOLVED", "ACCEPTED_DIFFERENCE", "FALSE_POSITIVE"];
    private const string RequiredRole = "APPROVER"; // ADR 0005

    private sealed record Locked(string RunId, string LegalEntityId, string Status, string RunState, int Version, bool Blocking);

    private static async Task<Locked?> LockAsync(NpgsqlTransaction tx, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT e.run_id, e.legal_entity_id, e.status, r.state, e.version, e.blocking
            FROM exceptions e JOIN runs r ON r.id = e.run_id WHERE e.id = @id FOR UPDATE OF e
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new Locked(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetInt32(4), rd.GetBoolean(5))
            : null;
    }

    private static CommandOutcome? Check(Locked? e, int expectedVersion)
    {
        if (e is null) { return new CommandOutcome(CommandStatus.NotFound); }
        if (e.Version != expectedVersion) { return new CommandOutcome(CommandStatus.Stale, "The exception changed since it was read; re-read it and retry."); }
        if (e.RunState == "SUPERSEDED") { return new CommandOutcome(CommandStatus.RunSuperseded, "The run is superseded; its exceptions are read-only."); }
        return null;
    }

    private static async Task<int> ExecAsync(NpgsqlTransaction tx, string sql, params (string, object?)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (k, v) in ps) { cmd.Parameters.AddWithValue(k, v ?? DBNull.Value); }
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlTransaction tx, string sql, params (string, object?)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (k, v) in ps) { cmd.Parameters.AddWithValue(k, v ?? DBNull.Value); }
        var o = await cmd.ExecuteScalarAsync();
        return o is null or DBNull ? default : (T)o;
    }

    private static Task TimelineAsync(NpgsqlTransaction tx, string exceptionId, string kind, string actorId, string summary) =>
        ExecAsync(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary, occurred_at) VALUES (@e, @k, 'USER', @a, @s, clock_timestamp())",
            ("e", exceptionId), ("k", kind), ("a", actorId), ("s", summary));

    private static async Task SetStatusAsync(NpgsqlTransaction tx, string id, int version, string status, string? resolutionKind, string? resolutionReason)
    {
        var n = await ExecAsync(tx, """
            UPDATE exceptions SET status = @s, resolution_kind = @k, resolution_reason = @r WHERE id = @id AND version = @v
            """, ("s", status), ("k", resolutionKind), ("r", resolutionReason), ("id", id), ("v", version));
        if (n != 1) { throw new InvalidOperationException("The locked exception row was not updated."); }
    }

    private async Task<AuditActor> ActorAsync(NpgsqlTransaction tx, string userId) =>
        new(ActorType.User, userId, await ScalarAsync<string>(tx, "SELECT name FROM users WHERE id = @id", ("id", userId)) ?? userId);

    private static string Json(params (string Key, string? Value)[] items) => JsonSerializer.Serialize(items.ToDictionary(i => i.Key, i => i.Value));

    public async Task<CommandOutcome> ProposeAsync(string exceptionId, int expectedVersion, string kind, string reason, IReadOnlyList<string> evidenceRefs, CommandContext ctx, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await LockAsync(tx, exceptionId, ct);
        if (Check(e, expectedVersion) is { } bad) { return bad; }
        if (Terminal.Contains(e!.Status)) { return new CommandOutcome(CommandStatus.InvalidState, $"The exception is {e.Status} and closed; reopen it first."); }

        // Re-proposing invalidates the pending proposal (and its approval) and keeps the working status to return to on rejection.
        var previous = e.Status;
        string? invalidated = null;
        if (e.Status == "PENDING_APPROVAL")
        {
            await using var q = new NpgsqlCommand("SELECT id, previous_status FROM resolution_proposals WHERE exception_id = @e AND status = 'PENDING' FOR UPDATE", conn, tx);
            q.Parameters.AddWithValue("e", exceptionId);
            await using (var r = await q.ExecuteReaderAsync(ct))
            {
                if (await r.ReadAsync(ct)) { invalidated = r.GetString(0); previous = r.IsDBNull(1) ? "OPEN" : r.GetString(1); }
            }
            if (invalidated is null) { previous = "OPEN"; }
            else
            {
                await ExecAsync(tx, "UPDATE resolution_proposals SET status = 'INVALIDATED' WHERE id = @id", ("id", invalidated));
                await ExecAsync(tx, "UPDATE approvals SET status = 'INVALIDATED' WHERE object_type = 'EXCEPTION_RESOLUTION' AND object_id = @id AND status = 'PENDING'", ("id", invalidated));
            }
        }

        var version = (int)(await ScalarAsync<long>(tx, "SELECT count(*) FROM resolution_proposals WHERE exception_id = @e", ("e", exceptionId)) + 1);
        var proposalId = "RP-" + Guid.NewGuid().ToString("N");
        await ExecAsync(tx, "INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id, previous_status) VALUES (@id, @e, @k, @r, @u, @p)",
            ("id", proposalId), ("e", exceptionId), ("k", kind), ("r", reason), ("u", ctx.ActorUserId), ("p", previous));
        var policy = await ScalarAsync<string>(tx, "SELECT config_version FROM config_rules WHERE kind = 'approval_policy' AND enabled ORDER BY updated_at DESC, id LIMIT 1") ?? "adr-0005";
        var approvalId = "AP-" + Guid.NewGuid().ToString("N");
        await using (var a = new NpgsqlCommand("""
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, rationale, evidence_refs, policy_version)
            VALUES (@id, 'EXCEPTION_RESOLUTION', @o, @run, @v, @u, @role, 'PENDING', @r, @refs, @pol)
            """, conn, tx))
        {
            a.Parameters.AddWithValue("id", approvalId);
            a.Parameters.AddWithValue("o", proposalId);
            a.Parameters.AddWithValue("run", e.RunId);
            a.Parameters.AddWithValue("v", version);
            a.Parameters.AddWithValue("u", ctx.ActorUserId);
            a.Parameters.AddWithValue("role", RequiredRole);
            a.Parameters.AddWithValue("r", reason);
            a.Parameters.Add(new NpgsqlParameter("refs", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(evidenceRefs) });
            a.Parameters.AddWithValue("pol", policy);
            await a.ExecuteNonQueryAsync(ct);
        }
        await SetStatusAsync(tx, exceptionId, expectedVersion, "PENDING_APPROVAL", null, null);

        await TimelineAsync(tx, exceptionId, "RESOLUTION_PROPOSED", ctx.ActorUserId,
            invalidated is null ? $"Proposed {kind}: {reason}" : $"Proposed {kind} (replaces the earlier proposal): {reason}");
        await audit.AppendAsync(tx, new AuditEventInput("RESOLUTION_PROPOSED", await ActorAsync(tx, ctx.ActorUserId), "EXCEPTION", exceptionId,
            $"Resolution {kind} proposed for exception {exceptionId}", reason, e.RunId, null, ctx.RequestId,
            Json(("status", e.Status), ("invalidatedProposalId", invalidated)),
            Json(("status", "PENDING_APPROVAL"), ("proposalId", proposalId), ("proposalVersion", version.ToString()), ("kind", kind), ("evidenceRefs", evidenceRefs.Count.ToString())),
            e.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }

    public async Task<CommandOutcome> DecideAsync(string exceptionId, string proposalId, int expectedVersion, bool approve, string reason, int? proposalVersion, CommandContext ctx, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await LockAsync(tx, exceptionId, ct);
        if (Check(e, expectedVersion) is { } bad) { return bad; }
        if (e!.Status != "PENDING_APPROVAL") { return new CommandOutcome(CommandStatus.InvalidState, "No resolution proposal is pending approval for this exception."); }

        string kind, pStatus, proposer; string? previous; int version; string approvalId;
        await using (var q = new NpgsqlCommand("""
            SELECT p.kind, p.status, p.proposed_by_user_id, p.previous_status, a.proposal_version, a.id
            FROM resolution_proposals p JOIN approvals a ON a.object_type = 'EXCEPTION_RESOLUTION' AND a.object_id = p.id
            WHERE p.id = @p AND p.exception_id = @e FOR UPDATE OF p, a
            """, conn, tx))
        {
            q.Parameters.AddWithValue("p", proposalId);
            q.Parameters.AddWithValue("e", exceptionId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { return new CommandOutcome(CommandStatus.NotFound); }
            kind = r.GetString(0); pStatus = r.GetString(1); proposer = r.GetString(2); previous = r.IsDBNull(3) ? null : r.GetString(3);
            version = r.GetInt32(4); approvalId = r.GetString(5);
        }
        if (pStatus != "PENDING") { return new CommandOutcome(CommandStatus.InvalidState, $"The proposal is {pStatus}; it was replaced or already decided."); }
        if (proposalVersion is { } pv && pv != version)
        {
            return new CommandOutcome(CommandStatus.InvalidState, $"The proposal changed (reviewed version {pv}, current {version}); re-read it and decide again.");
        }
        if (proposer == ctx.ActorUserId)
        {
            return new CommandOutcome(CommandStatus.Forbidden, "The proposer cannot decide their own resolution proposal.", "separation-of-duties");
        }

        var decision = approve ? "APPROVE" : "REJECT";
        await ExecAsync(tx, "UPDATE resolution_proposals SET status = @s WHERE id = @id", ("s", approve ? "APPROVED" : "REJECTED"), ("id", proposalId));
        await ExecAsync(tx, """
            UPDATE approvals SET status = @s, decision = @d, approver_user_id = @u, decided_at = clock_timestamp(), rationale = @r
            WHERE id = @id
            """, ("s", approve ? "APPROVED" : "REJECTED"), ("d", decision), ("u", ctx.ActorUserId), ("r", reason), ("id", approvalId));
        var newStatus = approve ? kind : (previous is null or "PENDING_APPROVAL" ? "OPEN" : previous);
        var propRationale = await ScalarAsync<string>(tx, "SELECT rationale FROM resolution_proposals WHERE id = @id", ("id", proposalId));
        await SetStatusAsync(tx, exceptionId, expectedVersion, newStatus, approve ? kind : null, approve ? propRationale : null);

        await TimelineAsync(tx, exceptionId, "APPROVAL", ctx.ActorUserId, approve ? $"Approved {kind}: {reason}" : $"Rejected {kind}: {reason}");
        await audit.AppendAsync(tx, new AuditEventInput("RESOLUTION_DECIDED", await ActorAsync(tx, ctx.ActorUserId), "EXCEPTION", exceptionId,
            $"Resolution {kind} {(approve ? "approved" : "rejected")} for exception {exceptionId}", reason, e.RunId, null, ctx.RequestId,
            Json(("status", "PENDING_APPROVAL"), ("proposalId", proposalId), ("proposedBy", proposer)),
            Json(("status", newStatus), ("decision", decision), ("proposalVersion", version.ToString())), e.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }

    public async Task<CommandOutcome> ReopenAsync(string exceptionId, int expectedVersion, string reason, CommandContext ctx, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await LockAsync(tx, exceptionId, ct);
        if (Check(e, expectedVersion) is { } bad) { return bad; }
        if (!Terminal.Contains(e!.Status)) { return new CommandOutcome(CommandStatus.InvalidState, $"The exception is {e.Status}; only a resolved, accepted or false-positive exception can be reopened."); }

        await SetStatusAsync(tx, exceptionId, expectedVersion, "OPEN", null, null);
        await TimelineAsync(tx, exceptionId, "REOPENED", ctx.ActorUserId, $"Reopened: {reason}");
        await audit.AppendAsync(tx, new AuditEventInput("EXCEPTION_REOPENED", await ActorAsync(tx, ctx.ActorUserId), "EXCEPTION", exceptionId,
            $"Exception {exceptionId} reopened", reason, e.RunId, null, ctx.RequestId,
            Json(("status", e.Status)), Json(("status", "OPEN")), e.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }

    public async Task<bool> HasUnresolvedBlockingExceptionsAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM exceptions WHERE run_id = @r AND blocking
                           AND status NOT IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE'))
            """);
        cmd.Parameters.AddWithValue("r", runId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
