using Npgsql;

namespace Proofly.Data.Exceptions;

/// <summary>
/// The one definition of "unresolved blocking exceptions of a run" used by run processing (READY_FOR_SIGN_OFF), the resolution
/// workflow and the sign-off gate: a <c>blocking</c> exception whose status is not RESOLVED, ACCEPTED_DIFFERENCE or FALSE_POSITIVE
/// (open, in investigation, waiting for data and pending approval all count). Non-blocking exceptions never block.
/// </summary>
public static class UnresolvedBlockingExceptions
{
    /// <summary>Terminal (closed) exception statuses as an SQL list.</summary>
    public const string TerminalStatusesSql = "('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE')";

    private const string Predicate = "run_id = @r AND blocking AND status NOT IN " + TerminalStatusesSql;

    public static async Task<bool> HasAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string runId, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM exceptions WHERE {Predicate})", conn, tx);
        cmd.Parameters.AddWithValue("r", runId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The same set as <see cref="HasAsync"/> as (id, status, control), ordered by id (ordinal).</summary>
    public static async Task<IReadOnlyList<(string Id, string Status, string Control)>> ListAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string runId, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand($"SELECT id, status, control FROM exceptions WHERE {Predicate} ORDER BY id COLLATE \"C\"", conn, tx);
        cmd.Parameters.AddWithValue("r", runId);
        var list = new List<(string, string, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { list.Add((r.GetString(0), r.GetString(1), r.GetString(2))); }
        return list;
    }
}
