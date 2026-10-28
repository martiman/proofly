using Npgsql;
using Proofly.Domain.Enums;

namespace Proofly.Data.Orchestration;

/// <summary>
/// How an exception continues from the run version it supersedes. Identity is the control's stable finding key within the
/// legal entity and business date: a finding with the same key in the new version is the same exception, linked through
/// <c>predecessor_exception_id</c>. The previous version's rows are never modified. The new exception receives:
/// <list type="bullet">
/// <item>the owner, notes, evidence references and the full timeline of the predecessor (history is kept);</item>
/// <item>the workflow status IN_INVESTIGATION or WAITING_FOR_DATA unchanged;</item>
/// <item>a terminal resolution (RESOLVED, ACCEPTED_DIFFERENCE, FALSE_POSITIVE) with its kind and reason ONLY if the finding is
/// materially identical: same reason code, severity, blocking flag, value and currency, and the same trace description (the
/// control's own statement of the facts). Otherwise the exception starts OPEN and the timeline says why the earlier resolution
/// no longer applies;</item>
/// <item>status OPEN for OPEN and PENDING_APPROVAL predecessors: a pending approval belongs to the old exception id and is
/// not transferred, the proposal has to be made again.</item>
/// </list>
/// A finding that no longer occurs is not carried (its exception stays on the old version and shows as resolved in the comparison).
/// </summary>
internal static class CarryOver
{
    public sealed record Predecessor(
        string Id, int RunVersion, string Status, string? OwnerUserId, string ReasonCode, string Severity, bool Blocking,
        decimal? ValueAmount, string? ValueCurrency, string? ResolutionKind, string? ResolutionReason, string? Description);

    public sealed record Decision(string Status, string? ResolutionKind, string? ResolutionReason, string Note);

    private static readonly HashSet<string> Terminal = ["RESOLVED", "ACCEPTED_DIFFERENCE", "FALSE_POSITIVE"];

    public static async Task<IReadOnlyDictionary<string, Predecessor>> LoadPredecessorAsync(NpgsqlTransaction tx, string predecessorRunId, CancellationToken ct)
    {
        var rows = await Sql.ReadAll(tx, """
            SELECT e.finding_key, e.id, r.run_version, e.status, e.owner_user_id, e.reason_code, e.severity, e.blocking, e.value_amount, e.value_currency::text,
                   e.resolution_kind, e.resolution_reason, t.description
            FROM exceptions e JOIN runs r ON r.id = e.run_id LEFT JOIN exception_rule_traces t ON t.exception_id = e.id
            WHERE e.run_id = @r AND e.finding_key IS NOT NULL
            """, ct, x => (Key: x.GetString(0), P: new Predecessor(x.GetString(1), x.GetInt32(2), x.GetString(3), x.IsDBNull(4) ? null : x.GetString(4), x.GetString(5),
                x.GetString(6), x.GetBoolean(7), x.IsDBNull(8) ? null : x.GetDecimal(8), x.IsDBNull(9) ? null : x.GetString(9),
                x.IsDBNull(10) ? null : x.GetString(10), x.IsDBNull(11) ? null : x.GetString(11), x.IsDBNull(12) ? null : x.GetString(12))), Sql.P("r", predecessorRunId));
        return rows.ToDictionary(r => r.Key, r => r.P, StringComparer.Ordinal);
    }

    public static Decision Decide(Predecessor p, ExceptionDraft e)
    {
        var f = e.Finding;
        if (Terminal.Contains(p.Status))
        {
            var same = p.ReasonCode == f.ReasonCode && p.Severity == WireNames.ToUpperSnake(f.Severity) && p.Blocking == f.Blocking
                       && p.ValueAmount == f.Value?.Amount && p.ValueCurrency == f.Value?.Currency && p.Description == f.Description;
            return same
                ? new Decision(p.Status, p.ResolutionKind, p.ResolutionReason, $"Continues {p.Id} (run version {p.RunVersion}); the {p.Status} resolution still applies because the finding is unchanged.")
                : new Decision("OPEN", null, null, $"Continues {p.Id} (run version {p.RunVersion}); its {p.Status} resolution no longer applies because the finding changed. Review again.");
        }
        return p.Status is "IN_INVESTIGATION" or "WAITING_FOR_DATA"
            ? new Decision(p.Status, null, null, $"Continues {p.Id} (run version {p.RunVersion}); status {p.Status} kept.")
            : new Decision("OPEN", null, null, $"Continues {p.Id} (run version {p.RunVersion}); status {p.Status} is not carried, propose again if still needed.");
    }

    public static async Task CopyHistoryAsync(NpgsqlTransaction tx, Predecessor p, string newId, Decision d, CancellationToken ct)
    {
        await Sql.Exec(tx, "INSERT INTO exception_notes (exception_id, author_user_id, body, created_at) SELECT @n, author_user_id, body, created_at FROM exception_notes WHERE exception_id=@o ORDER BY id", ct,
            Sql.P("n", newId), Sql.P("o", p.Id));
        await Sql.Exec(tx, "INSERT INTO exception_evidence_refs (exception_id, kind, ref, created_at) SELECT @n, kind, ref, created_at FROM exception_evidence_refs WHERE exception_id=@o ORDER BY id", ct,
            Sql.P("n", newId), Sql.P("o", p.Id));
        await Sql.Exec(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary, occurred_at) SELECT @n, kind, actor_type, actor_id, summary, occurred_at FROM exception_timeline WHERE exception_id=@o ORDER BY id", ct,
            Sql.P("n", newId), Sql.P("o", p.Id));
        await Sql.Exec(tx, "INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary) VALUES (@n, 'SYSTEM', 'SYSTEM', NULL, @s)", ct,
            Sql.P("n", newId), Sql.P("s", d.Note));
    }
}
