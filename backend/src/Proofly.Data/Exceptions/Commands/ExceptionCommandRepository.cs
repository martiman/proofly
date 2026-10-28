using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Proofly.Data.Audit;
using Proofly.Domain.Enums;

namespace Proofly.Data.Exceptions.Commands;

/// <summary>
/// Write side of the exceptions queue. One transaction per command: lock the row, check version (If-Match) and state, change,
/// append the timeline entry and the audit event, commit. Any non-Ok outcome rolls everything back.
/// </summary>
public interface IExceptionCommandRepository
{
    Task<ExceptionRef?> FindAsync(string exceptionId, CancellationToken ct = default);
    Task<CommandOutcome> AssignAsync(string exceptionId, int expectedVersion, string? ownerId, string? note, CommandContext ctx, CancellationToken ct = default);
    Task<CommandOutcome> AddNoteAsync(string exceptionId, int expectedVersion, string text, CommandContext ctx, CancellationToken ct = default);
    Task<CommandOutcome> RequestEvidenceAsync(string exceptionId, int expectedVersion, string text, string? requestedFrom, CommandContext ctx, CancellationToken ct = default);

    Task<IReadOnlyList<SavedViewRow>> ListViewsAsync(string userId, CancellationToken ct = default);
    Task<(CommandOutcome Outcome, SavedViewRow? View)> CreateViewAsync(string userId, string name, string filtersJson, string? sort, CancellationToken ct = default);
    /// <summary>Only the owner may delete. NotFound when absent, Forbidden for another user's view, Stale on an If-Match mismatch.</summary>
    Task<CommandOutcome> DeleteViewAsync(string viewId, string userId, int? expectedVersion, CancellationToken ct = default);
    Task<SavedViewRow?> FindViewAsync(string viewId, string userId, CancellationToken ct = default);
}

public static class ExceptionCommandsDataModule
{
    public const int MaxViewsPerUser = 50;
    public static IServiceCollection AddExceptionCommandsData(this IServiceCollection s)
    {
        s.AddSingleton<IExceptionCommandRepository, ExceptionCommandRepository>();
        return s;
    }
}

public sealed class ExceptionCommandRepository(NpgsqlDataSource db, IAuditWriter audit) : IExceptionCommandRepository
{
    private static readonly string[] Terminal = ["RESOLVED", "ACCEPTED_DIFFERENCE", "FALSE_POSITIVE"];

    public async Task<ExceptionRef?> FindAsync(string exceptionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, run_id, legal_entity_id, version FROM exceptions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", exceptionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ExceptionRef(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)) : null;
    }

    private sealed record Locked(string RunId, string LegalEntityId, string Status, string? OwnerId, string RunState, int Version);

    /// <summary>Locks the exception row; null when it does not exist. The version is compared by the caller of this helper.</summary>
    private static async Task<Locked?> LockAsync(NpgsqlTransaction tx, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT e.run_id, e.legal_entity_id, e.status, e.owner_user_id, r.state, e.version
            FROM exceptions e JOIN runs r ON r.id = e.run_id WHERE e.id = @id FOR UPDATE OF e
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new Locked(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4), rd.GetInt32(5))
            : null;
    }

    private static CommandOutcome? Precheck(Locked? e, int expectedVersion, bool allowPendingApproval, out Locked locked)
    {
        locked = e!;
        if (e is null) { return new CommandOutcome(CommandStatus.NotFound); }
        if (e.Version != expectedVersion) { return new CommandOutcome(CommandStatus.Stale, "The exception changed since it was read; re-read it and retry."); }
        if (e.RunState == "SUPERSEDED") { return new CommandOutcome(CommandStatus.RunSuperseded, "The run is superseded; its exceptions are read-only."); }
        if (e.RunState == "SIGNED_OFF") { return new CommandOutcome(CommandStatus.InvalidState, "The run is signed off and immutable; reprocess the run to change its exceptions."); }
        if (Terminal.Contains(e.Status)) { return new CommandOutcome(CommandStatus.InvalidState, $"The exception is {e.Status} and closed; reopen it first."); }
        if (!allowPendingApproval && e.Status == "PENDING_APPROVAL") { return new CommandOutcome(CommandStatus.InvalidState, "A resolution proposal is pending approval; decide it first."); }
        return null;
    }

    private static async Task<string> ActorNameAsync(NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT name FROM users WHERE id = @id", tx.Connection, tx);
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteScalarAsync(ct) as string ?? userId;
    }

    private static async Task TimelineAsync(NpgsqlTransaction tx, string exceptionId, string kind, string actorId, string summary, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary, occurred_at) VALUES (@e, @k, 'USER', @a, @s, clock_timestamp())", tx.Connection, tx);
        cmd.Parameters.AddWithValue("e", exceptionId);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("a", actorId);
        cmd.Parameters.AddWithValue("s", summary);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAsync(NpgsqlTransaction tx, string id, int version, string? newStatus, bool setOwner, string? owner, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE exceptions SET status = COALESCE(@status, status), owner_user_id = CASE WHEN @setOwner THEN @owner ELSE owner_user_id END
            WHERE id = @id AND version = @v
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("v", version);
        cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)newStatus ?? DBNull.Value });
        cmd.Parameters.AddWithValue("setOwner", setOwner);
        cmd.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = (object?)owner ?? DBNull.Value });
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The locked exception row was not updated."); }
    }

    private static string Json(params (string Key, string? Value)[] items) =>
        JsonSerializer.Serialize(items.ToDictionary(i => i.Key, i => i.Value));

    public async Task<CommandOutcome> AssignAsync(string exceptionId, int expectedVersion, string? ownerId, string? note, CommandContext ctx, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await LockAsync(tx, exceptionId, ct);
        if (Precheck(e, expectedVersion, allowPendingApproval: true, out var l) is { } bad) { return bad; }
        e = l;

        string? ownerName = null;
        if (ownerId is not null)
        {
            await using var u = new NpgsqlCommand("""
                SELECT u.name FROM users u WHERE u.id = @o AND u.active
                  AND EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = u.id AND ur.legal_entity_id IN (@le, '*'))
                """, conn, tx);
            u.Parameters.AddWithValue("o", ownerId);
            u.Parameters.AddWithValue("le", e.LegalEntityId);
            ownerName = await u.ExecuteScalarAsync(ct) as string;
            if (ownerName is null)
            {
                return new CommandOutcome(CommandStatus.Invalid, "The owner is not an active user with a role in this legal entity.", "/ownerId");
            }
        }

        await UpdateAsync(tx, exceptionId, expectedVersion, null, true, ownerId, ct);
        await using (var a = new NpgsqlCommand(
            "INSERT INTO exception_assignments (exception_id, assignee_user_id, assigned_by_user_id, reason) VALUES (@e, @o, @by, @r)", conn, tx))
        {
            a.Parameters.AddWithValue("e", exceptionId);
            a.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Text) { Value = (object?)ownerId ?? DBNull.Value });
            a.Parameters.AddWithValue("by", ctx.ActorUserId);
            a.Parameters.AddWithValue("r", note ?? (ownerId is null ? "Unassigned" : "Assigned"));
            await a.ExecuteNonQueryAsync(ct);
        }

        var summary = ownerId is null ? "Unassigned" : $"Assigned to {ownerName}";
        await TimelineAsync(tx, exceptionId, "ASSIGNMENT", ctx.ActorUserId, note is null ? summary : $"{summary}: {note}", ct);
        await audit.AppendAsync(tx, new AuditEventInput("EXCEPTION_ASSIGNED", new AuditActor(ActorType.User, ctx.ActorUserId, await ActorNameAsync(tx, ctx.ActorUserId, ct)),
            "EXCEPTION", exceptionId, $"Exception {exceptionId}: {summary.ToLowerInvariant()}", note, e.RunId, null, ctx.RequestId,
            Json(("ownerId", e.OwnerId)), Json(("ownerId", ownerId)), e.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }

    public async Task<CommandOutcome> AddNoteAsync(string exceptionId, int expectedVersion, string text, CommandContext ctx, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await LockAsync(tx, exceptionId, ct);
        if (Precheck(e, expectedVersion, allowPendingApproval: true, out var l) is { } bad) { return bad; }
        e = l;

        long noteId;
        await using (var n = new NpgsqlCommand("INSERT INTO exception_notes (exception_id, author_user_id, body) VALUES (@e, @a, @b) RETURNING id", conn, tx))
        {
            n.Parameters.AddWithValue("e", exceptionId);
            n.Parameters.AddWithValue("a", ctx.ActorUserId);
            n.Parameters.AddWithValue("b", text);
            noteId = (long)(await n.ExecuteScalarAsync(ct))!;
        }
        await UpdateAsync(tx, exceptionId, expectedVersion, null, false, null, ct); // touches the row so the ETag moves
        await TimelineAsync(tx, exceptionId, "NOTE", ctx.ActorUserId, text, ct);
        await audit.AppendAsync(tx, new AuditEventInput("NOTE_ADDED", new AuditActor(ActorType.User, ctx.ActorUserId, await ActorNameAsync(tx, ctx.ActorUserId, ct)),
            "EXCEPTION", exceptionId, $"Note added to exception {exceptionId}", null, e.RunId, null, ctx.RequestId,
            null, Json(("noteId", noteId.ToString()), ("length", text.Length.ToString())), e.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }

    public async Task<CommandOutcome> RequestEvidenceAsync(string exceptionId, int expectedVersion, string text, string? requestedFrom, CommandContext ctx, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await LockAsync(tx, exceptionId, ct);
        if (Precheck(e, expectedVersion, allowPendingApproval: false, out var l) is { } bad) { return bad; }
        e = l;

        await UpdateAsync(tx, exceptionId, expectedVersion, "WAITING_FOR_DATA", false, null, ct);
        var summary = requestedFrom is null ? $"Evidence requested: {text}" : $"Evidence requested from {requestedFrom}: {text}";
        await TimelineAsync(tx, exceptionId, "EVIDENCE_REQUEST", ctx.ActorUserId, summary, ct);
        await audit.AppendAsync(tx, new AuditEventInput("EVIDENCE_REQUESTED", new AuditActor(ActorType.User, ctx.ActorUserId, await ActorNameAsync(tx, ctx.ActorUserId, ct)),
            "EXCEPTION", exceptionId, $"Evidence requested for exception {exceptionId}", text.Trim().Length >= 3 ? text : null, e.RunId, null, ctx.RequestId,
            Json(("status", e.Status)), Json(("status", "WAITING_FOR_DATA"), ("requestedFrom", requestedFrom)), e.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }

    // ---- saved views (personal preference: no audit event, see the contract) ----

    private const string ViewSelect = """
        SELECT v.id, v.user_id, u.name, v.name, v.shared, v.filters::text, v.sort, v.created_at, v.version
        FROM saved_views v JOIN users u ON u.id = v.user_id
        """;

    private static SavedViewRow ReadView(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetInt32(8));

    public async Task<IReadOnlyList<SavedViewRow>> ListViewsAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{ViewSelect} WHERE v.scope = 'exceptions' AND (v.user_id = @u OR v.shared) ORDER BY v.created_at, v.id");
        cmd.Parameters.AddWithValue("u", userId);
        var list = new List<SavedViewRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { list.Add(ReadView(r)); }
        return list;
    }

    public async Task<SavedViewRow?> FindViewAsync(string viewId, string userId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{ViewSelect} WHERE v.id = @id AND v.scope = 'exceptions' AND (v.user_id = @u OR v.shared)");
        cmd.Parameters.AddWithValue("id", viewId);
        cmd.Parameters.AddWithValue("u", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadView(r) : null;
    }

    public async Task<(CommandOutcome Outcome, SavedViewRow? View)> CreateViewAsync(string userId, string name, string filtersJson, string? sort, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Serialise creations per user so the 50-view cap cannot be exceeded by concurrent requests.
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@k))", conn, tx))
        {
            lk.Parameters.AddWithValue("k", "saved_views:" + userId);
            await lk.ExecuteNonQueryAsync(ct);
        }
        await using (var cnt = new NpgsqlCommand("SELECT count(*) FROM saved_views WHERE user_id = @u AND scope = 'exceptions'", conn, tx))
        {
            cnt.Parameters.AddWithValue("u", userId);
            if ((long)(await cnt.ExecuteScalarAsync(ct))! >= ExceptionCommandsDataModule.MaxViewsPerUser)
            {
                return (new CommandOutcome(CommandStatus.LimitReached, $"A user can keep at most {ExceptionCommandsDataModule.MaxViewsPerUser} saved views; delete one first.", "/name"), null);
            }
        }
        await using (var dup = new NpgsqlCommand("SELECT 1 FROM saved_views WHERE user_id = @u AND scope = 'exceptions' AND name = @n", conn, tx))
        {
            dup.Parameters.AddWithValue("u", userId);
            dup.Parameters.AddWithValue("n", name);
            if (await dup.ExecuteScalarAsync(ct) is not null) { return (new CommandOutcome(CommandStatus.Duplicate, "You already have a saved view with this name.", "/name"), null); }
        }
        var id = "SV-" + Guid.NewGuid().ToString("N");
        await using (var ins = new NpgsqlCommand("INSERT INTO saved_views (id, user_id, scope, name, filters, shared, sort) VALUES (@id, @u, 'exceptions', @n, @f, false, @s)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("u", userId);
            ins.Parameters.AddWithValue("n", name);
            ins.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Jsonb) { Value = filtersJson });
            ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)sort ?? DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
        }
        SavedViewRow row;
        await using (var sel = new NpgsqlCommand($"{ViewSelect} WHERE v.id = @id", conn, tx))
        {
            sel.Parameters.AddWithValue("id", id);
            await using var r = await sel.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            row = ReadView(r);
        }
        await tx.CommitAsync(ct);
        return (CommandOutcome.Ok, row);
    }

    public async Task<CommandOutcome> DeleteViewAsync(string viewId, string userId, int? expectedVersion, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string owner; int version;
        await using (var sel = new NpgsqlCommand("SELECT user_id, version FROM saved_views WHERE id = @id AND scope = 'exceptions' FOR UPDATE", conn, tx))
        {
            sel.Parameters.AddWithValue("id", viewId);
            await using var r = await sel.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { return new CommandOutcome(CommandStatus.NotFound); }
            owner = r.GetString(0);
            version = r.GetInt32(1);
        }
        await using (var vis = new NpgsqlCommand("SELECT shared FROM saved_views WHERE id = @id", conn, tx))
        {
            vis.Parameters.AddWithValue("id", viewId);
            var shared = (bool)(await vis.ExecuteScalarAsync(ct))!;
            if (owner != userId) { return shared ? new CommandOutcome(CommandStatus.Forbidden, "Only the owner can delete a saved view.") : new CommandOutcome(CommandStatus.NotFound); }
        }
        if (expectedVersion is { } v && v != version) { return new CommandOutcome(CommandStatus.Stale, "The saved view changed since it was read."); }
        await using (var del = new NpgsqlCommand("DELETE FROM saved_views WHERE id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", viewId);
            await del.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return CommandOutcome.Ok;
    }
}
