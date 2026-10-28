using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Proofly.Data.Audit;
using Proofly.Data.Exceptions;
using Proofly.Domain.Enums;

namespace Proofly.Data.Runs.Commands;

public interface ISignOffRepository
{
    /// <summary>Null when the run does not exist or its legal entity is outside the scope.</summary>
    Task<SignOffPackRows?> GetPackAsync(EntityScope scope, string runId, CancellationToken ct = default);
    /// <summary>The pack with at most <paramref name="openMattersLimit"/> open matters (blocking first, then severity, then value); the totals count all of them.</summary>
    Task<SignOffPackRows?> GetPackAsync(EntityScope scope, string runId, int openMattersLimit, CancellationToken ct = default);
    /// <summary>One keyset page of the open matters of an in-scope run, in the order of the pack; null when the run does not exist or is out of scope.</summary>
    Task<OpenMatterPageRows?> ListOpenMattersAsync(EntityScope scope, string runId, OpenMatterFilter filter, int limit, OpenMatterCursor? after, bool includeTotal, CancellationToken ct = default);
    Task<SignOffResult> SubmitAsync(string runId, string? requiredApproverId, string? note, SignOffCommandContext ctx, CancellationToken ct = default);
    Task<SignOffResult> ReturnAsync(string runId, string reason, SignOffCommandContext ctx, CancellationToken ct = default);
    Task<SignOffResult> ApproveAsync(string runId, string reason, string? attestationTextHash, SignOffCommandContext ctx, CancellationToken ct = default);
}

public static class SignOffDataModule
{
    public static IServiceCollection AddSignOffData(this IServiceCollection s)
    {
        s.AddRunsData();
        s.TryAddSingleton<ISignOffRepository, SignOffRepository>();
        return s;
    }
}

/// <summary>
/// Run sign-off. One transaction per command: lock the run (and its latest sign-off row), compare the versions the caller read,
/// validate the state, change, write the audit event, commit. Sign-off rows are never edited once SIGNED_OFF.
/// </summary>
public sealed class SignOffRepository(NpgsqlDataSource db, IAuditWriter audit) : ISignOffRepository
{
    public const string PolicyVersion = "adr-0005-mvp";
    private const string TerminalSql = UnresolvedBlockingExceptions.TerminalStatusesSql;
    private static readonly HashSet<string> Terminal = ["RESOLVED", "ACCEPTED_DIFFERENCE", "FALSE_POSITIVE"];
    private static bool IsOpen(string status) => !Terminal.Contains(status);

    private static NpgsqlCommand Cmd(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in ps) { cmd.Parameters.AddWithValue(n, v ?? DBNull.Value); }
        return cmd;
    }

    // ---------------------------------------------------------------- shared reads

    private sealed record RunLock(string LegalEntityId, DateOnly BusinessDate, string State, string SignOffState, int Version, string? ResultHash, string RulesetVersion);

    private static async Task<RunLock?> LockRunAsync(NpgsqlConnection c, NpgsqlTransaction tx, string runId, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "SELECT legal_entity_id, business_date, state, sign_off_state, version, result_hash, ruleset_version FROM runs WHERE id=@id FOR UPDATE", ("id", runId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new RunLock(r.GetString(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetString(2), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6))
            : null;
    }

    private const string SignOffColumns = """
        id, state, prepared_by_user_id, required_approver_user_id, approval_id, evidence_pack_id, version,
        submitted_result_hash, submitted_open_set_hash, submitted_exception_state_hash, attestation_hash, returned_reason
        """;

    private static SignOffRow ReadSignOff(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetString(1), N(r, 2), N(r, 3), N(r, 4), N(r, 5), r.GetInt32(6), N(r, 7), N(r, 8), N(r, 9), N(r, 10), N(r, 11));
    private static string? N(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static async Task<SignOffRow?> LatestSignOffAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string runId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, $"SELECT {SignOffColumns} FROM run_signoffs WHERE run_id=@r ORDER BY created_at DESC, id DESC LIMIT 1" + (forUpdate ? " FOR UPDATE" : ""), ("r", runId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSignOff(r) : null;
    }

    private static async Task<List<(string Id, string Status, bool Blocking)>> ExceptionStatesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string runId, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "SELECT id, status, blocking FROM exceptions WHERE run_id=@r", ("r", runId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(string, string, bool)>();
        while (await r.ReadAsync(ct)) { list.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2))); }
        return list;
    }

    /// <summary>Why the run cannot be signed off right now (empty = nothing blocks). Strict = the run must already be READY_FOR_SIGN_OFF.</summary>
    private static async Task<List<string>> BlockersAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string runId, RunLock run, bool strict, CancellationToken ct)
    {
        var blockers = new List<string>();
        var stateOk = run.State == "READY_FOR_SIGN_OFF"
            || (!strict && run.State == "REVIEW_REQUIRED" && run.SignOffState is "RETURNED" or "PREPARATION_INCOMPLETE");
        if (!stateOk) { blockers.Add($"The run is {run.State}; it must be READY_FOR_SIGN_OFF."); }
        if (run.ResultHash is null) { blockers.Add("The run has no result hash; it has not finished processing."); }

        await using (var cmd = Cmd(c, tx, "SELECT state, blocking_finding_count FROM run_control_results WHERE run_id=@r AND control='data_completeness'", ("r", runId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) { blockers.Add("The data completeness result is missing."); }
            else
            {
                var state = r.GetString(0);
                var blocking = r.GetInt32(1);
                if (!(state == "PASSED" || (state == "QUALIFIED" && blocking == 0))) { blockers.Add($"Data completeness is {state}; it must be PASSED or QUALIFIED without blocking findings."); }
            }
        }

        foreach (var (id, status) in await UnresolvedBlockingExceptions.ListAsync(c, tx, runId, ct)) { blockers.Add($"Blocking exception {id} is unresolved ({status})."); }
        return blockers;
    }

    private static async Task<string> UserNameAsync(NpgsqlConnection c, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "SELECT name FROM users WHERE id=@id", ("id", userId));
        return await cmd.ExecuteScalarAsync(ct) as string ?? userId;
    }

    private static async Task<string?> LatestEvidencePackAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string runId, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "SELECT id FROM evidence_packs WHERE run_id=@r ORDER BY created_at DESC, id DESC LIMIT 1", ("r", runId));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---------------------------------------------------------------- read

    public const int OpenMattersDefaultLimit = 50;

    // Open-matter order. The sort keys are the expressions of ix_exceptions_run_open_matters (migration 0049), so the first page is an index range scan.
    private const string KeyBlocking = "(CASE WHEN e.blocking THEN 0 ELSE 1 END)";
    private const string KeySeverity = "(CASE e.severity WHEN 'CRITICAL' THEN 0 WHEN 'HIGH' THEN 1 WHEN 'MEDIUM' THEN 2 ELSE 3 END)";
    private const string KeyValue = "(-COALESCE(abs(e.value_amount), -1))";
    private const string KeyId = "(e.id COLLATE \"C\")";
    private const string OpenMatterOrder = $"{KeyBlocking}, {KeySeverity}, {KeyValue}, {KeyId}";

    private const string ExceptionColumns = """
        e.id, e.run_id, e.title, e.control, e.reason_code, e.severity, e.status, e.value_amount, e.value_currency, e.legal_entity_id, e.business_date,
        e.owner_user_id, u.name, e.created_at, GREATEST(0, (now()::date - e.created_at::date))::int, e.due_at, e.blocking, e.source_file, e.source_row_id, e.version
        """;

    private static ExceptionRow ReadException(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
            r.IsDBNull(7) ? null : r.GetDecimal(7), N(r, 8), r.GetString(9), DateOnly.FromDateTime(r.GetDateTime(10)), N(r, 11), N(r, 12),
            r.GetFieldValue<DateTimeOffset>(13), r.GetInt32(14), r.GetFieldValue<DateTimeOffset>(15), r.GetBoolean(16), N(r, 17), N(r, 18), r.GetInt32(19));

    public Task<SignOffPackRows?> GetPackAsync(EntityScope scope, string runId, CancellationToken ct = default) => GetPackAsync(scope, runId, OpenMattersDefaultLimit, ct);

    public async Task<OpenMatterPageRows?> ListOpenMattersAsync(EntityScope scope, string runId, OpenMatterFilter filter, int limit, OpenMatterCursor? after, bool includeTotal, CancellationToken ct = default)
    {
        var run = await new RunReadRepository(db, TimeProvider.System).GetRunAsync(scope, runId, ct);
        if (run is null) { return null; }
        await using var c = await db.OpenConnectionAsync(ct);
        const string filterSql = $"e.run_id=@r AND e.status NOT IN {TerminalSql} AND (@blk::boolean IS NULL OR e.blocking = @blk) AND (@sev::text IS NULL OR e.severity = @sev)";
        void Bind(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.Add(new NpgsqlParameter("blk", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)filter.Blocking ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("sev", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)filter.Severity ?? DBNull.Value });
        }

        var items = new List<OpenMatterItem>();
        await using (var cmd = Cmd(c, null, $"""
            SELECT {ExceptionColumns}, {KeyBlocking}, {KeySeverity}, {KeyValue}
            FROM exceptions e LEFT JOIN users u ON u.id = e.owner_user_id
            WHERE {filterSql}
              AND (@hasCur = false OR ({OpenMatterOrder}) > (@c1, @c2, @c3, @c4))
            ORDER BY {OpenMatterOrder}
            LIMIT @lim
            """))
        {
            Bind(cmd);
            cmd.Parameters.AddWithValue("hasCur", after is not null);
            cmd.Parameters.Add(new NpgsqlParameter("c1", NpgsqlTypes.NpgsqlDbType.Integer) { Value = after?.Blocking ?? 0 });
            cmd.Parameters.Add(new NpgsqlParameter("c2", NpgsqlTypes.NpgsqlDbType.Integer) { Value = after?.Severity ?? 0 });
            cmd.Parameters.Add(new NpgsqlParameter("c3", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = after?.NegAbs ?? 0m });
            cmd.Parameters.Add(new NpgsqlParameter("c4", NpgsqlTypes.NpgsqlDbType.Text) { Value = after?.Id ?? "" });
            cmd.Parameters.AddWithValue("lim", limit + 1);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { items.Add(new OpenMatterItem(ReadException(r), new OpenMatterCursor(r.GetInt32(20), r.GetInt32(21), r.GetDecimal(22), r.GetString(0)))); }
        }

        int? total = null;
        if (includeTotal)
        {
            await using var cmd = Cmd(c, null, $"SELECT count(*) FROM exceptions e WHERE {filterSql}");
            Bind(cmd);
            total = (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return new OpenMatterPageRows(items, total, run.LegalEntityId);
    }

    public async Task<SignOffPackRows?> GetPackAsync(EntityScope scope, string runId, int openMattersLimit, CancellationToken ct = default)
    {
        var run = await new RunReadRepository(db, TimeProvider.System).GetRunAsync(scope, runId, ct);
        if (run is null) { return null; }
        await using var c = await db.OpenConnectionAsync(ct);
        var so = await LatestSignOffAsync(c, null, runId, false, ct);

        // The open set is summarised by SQL aggregates; only the top matters are materialised.
        var open = new List<ExceptionRow>();
        await using (var cmd = Cmd(c, null, $"""
            SELECT {ExceptionColumns}
            FROM exceptions e LEFT JOIN users u ON u.id = e.owner_user_id
            WHERE e.run_id=@r AND e.status NOT IN {TerminalSql}
            ORDER BY {OpenMatterOrder}
            LIMIT @lim
            """, ("r", runId), ("lim", openMattersLimit)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { open.Add(ReadException(r)); }
        }

        var byCategory = new List<OpenMatterCategoryRow>();
        await using (var cmd = Cmd(c, null, $"SELECT e.control, count(*), count(*) FILTER (WHERE e.blocking) FROM exceptions e WHERE e.run_id=@r AND e.status NOT IN {TerminalSql} GROUP BY e.control ORDER BY e.control", ("r", runId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { byCategory.Add(new OpenMatterCategoryRow(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2))); }
        }

        var checklist = new List<ChecklistRow>();
        var qualifications = new List<string>();
        await using (var cmd = Cmd(c, null, "SELECT control, state, finding_count, blocking_finding_count FROM run_control_results WHERE run_id=@r ORDER BY control", ("r", runId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var control = r.GetString(0);
                checklist.Add(new ChecklistRow(control, r.GetString(1), r.GetInt32(2), r.GetInt32(3), [], byCategory.Where(x => x.Control == control).Sum(x => x.Total)));
                if (r.GetString(1) == "QUALIFIED") { qualifications.Add($"{control} is qualified ({r.GetInt32(2)} findings)"); }
            }
        }
        for (var i = 0; i < checklist.Count; i++)
        {
            if (checklist[i].OpenCount > 0) { checklist[i] = checklist[i] with { ExceptionIds = await ControlOpenIdsAsync(c, runId, checklist[i].Control, openMattersLimit, ct) }; }
        }

        var text = SignOffAttestation.Text(run.Id, run.LegalEntityId, run.BusinessDate, run.RulesetVersion, await ResultHashAsync(c, runId, ct), qualifications);
        var textHash = SignOffAttestation.Sha256Hex(text);

        var resultHash = await ResultHashAsync(c, runId, ct);
        var changes = new List<string>();
        if (so?.SubmittedResultHash is not null && so.State == "APPROVAL_PENDING")
        {
            // The hashes need every exception of the run, so they are only computed when there is a submission to compare with.
            var all = await ExceptionStatesAsync(c, null, runId, ct);
            var open2 = SignOffAttestation.OpenSetHash(all, IsOpen);
            var state2 = SignOffAttestation.ExceptionStateHash(all);
            if (so.SubmittedResultHash != resultHash) { changes.Add("The run result changed since submission."); }
            if (so.SubmittedOpenSetHash != open2) { changes.Add("The set of open exceptions changed since submission."); }
            if (so.SubmittedExceptionStateHash != state2) { changes.Add("An exception changed state since submission."); }
        }

        List<string> blockers = [];
        if (so?.State is not ("SIGNED_OFF" or "SUPERSEDED" or "INVALIDATED") && run.State != "SUPERSEDED")
        {
            var lockLike = new RunLock(run.LegalEntityId, run.BusinessDate, run.State, run.SignOffState, 0, resultHash, run.RulesetVersion);
            blockers = await BlockersAsync(c, null, runId, lockLike, strict: false, ct);
        }

        SignOffApprovalRow? approval = null;
        if (so?.ApprovalId is not null)
        {
            await using var cmd = Cmd(c, null, """
                SELECT a.id, a.object_type, a.object_id, a.run_id, a.proposal_version, a.proposer_user_id, p.name, a.approver_user_id, ap.name, a.required_role,
                       a.status, a.decision, a.rationale, a.policy_version, a.proposed_at, a.decided_at
                FROM approvals a JOIN users p ON p.id = a.proposer_user_id LEFT JOIN users ap ON ap.id = a.approver_user_id WHERE a.id=@id
                """, ("id", so.ApprovalId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                approval = new SignOffApprovalRow(r.GetString(0), r.GetString(1), r.GetString(2), N(r, 3), r.GetInt32(4), new UserRefRow(r.GetString(5), r.GetString(6)),
                    r.IsDBNull(7) ? null : new UserRefRow(r.GetString(7), r.GetString(8)), r.GetString(9), r.GetString(10), N(r, 11), N(r, 12), r.GetString(13),
                    r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15));
            }
        }

        var history = new List<SignOffHistoryRow>();
        await using (var cmd = Cmd(c, null, """
            SELECT a.id, a.occurred_at, a.event_type, a.actor_id, COALESCE(u.name, a.actor_id, 'system'), a.actor_type, a.reason
            FROM audit_events a LEFT JOIN users u ON u.id = a.actor_id
            WHERE a.run_id=@r AND a.event_type IN ('SIGN_OFF_SUBMITTED','SIGN_OFF_RETURNED','SIGN_OFF_INVALIDATED','RUN_SIGNED_OFF') ORDER BY a.occurred_at, a.id
            """, ("r", runId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { history.Add(new SignOffHistoryRow(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), N(r, 3), r.GetString(4), r.GetString(5), N(r, 6))); }
        }

        return new SignOffPackRows(run, so, checklist, open, byCategory.Sum(x => x.Total), byCategory, qualifications, changes, blockers, text, textHash,
            so?.EvidencePackId ?? await LatestEvidencePackAsync(c, null, runId, ct), approval, history, changes.Count > 0);
    }

    /// <summary>Ids of the first open exceptions of one control in open-matter order (a checklist item lists at most <paramref name="limit"/>).</summary>
    private static async Task<List<string>> ControlOpenIdsAsync(NpgsqlConnection c, string runId, string control, int limit, CancellationToken ct)
    {
        await using var cmd = Cmd(c, null, $"SELECT e.id FROM exceptions e WHERE e.run_id=@r AND e.control=@c AND e.status NOT IN {TerminalSql} ORDER BY {OpenMatterOrder} LIMIT @lim",
            ("r", runId), ("c", control), ("lim", limit));
        var ids = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { ids.Add(r.GetString(0)); }
        return ids;
    }

    private static async Task<string?> ResultHashAsync(NpgsqlConnection c, string runId, CancellationToken ct)
    {
        await using var cmd = Cmd(c, null, "SELECT result_hash FROM runs WHERE id=@r", ("r", runId));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---------------------------------------------------------------- commands

    private async Task<AuditActor> ActorAsync(NpgsqlConnection c, NpgsqlTransaction tx, string userId, CancellationToken ct) =>
        new(ActorType.User, userId, await UserNameAsync(c, tx, userId, ct));

    private static bool VersionsDiffer(RunLock run, SignOffRow? so, SignOffCommandContext ctx) =>
        run.Version != ctx.ExpectedRunVersion || (so?.Version ?? 0) != ctx.ExpectedSignOffVersion;

    private static SignOffResult VersionMismatch() => new(SignOffStatus.VersionMismatch, "The sign-off changed since it was read; re-read it and retry.");

    private static string Join(IEnumerable<string> blockers) => string.Join(" ", blockers);

    public async Task<SignOffResult> SubmitAsync(string runId, string? requiredApproverId, string? note, SignOffCommandContext ctx, CancellationToken ct = default)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var run = await LockRunAsync(c, tx, runId, ct);
        if (run is null) { return new SignOffResult(SignOffStatus.NotFound); }
        var so = await LatestSignOffAsync(c, tx, runId, true, ct);
        if (VersionsDiffer(run, so, ctx)) { return VersionMismatch(); }
        if (run.State == "SUPERSEDED") { return new SignOffResult(SignOffStatus.RunSuperseded, "The run is superseded; its sign-off is read-only."); }
        if (run.SignOffState == "SIGNED_OFF") { return new SignOffResult(SignOffStatus.InvalidState, "The run is signed off and immutable; reprocess it to change the result."); }
        if (run.SignOffState == "APPROVAL_PENDING") { return new SignOffResult(SignOffStatus.InvalidState, "The run is already submitted and waits for approval."); }
        if (run.SignOffState is "INVALIDATED" or "SUPERSEDED") { return new SignOffResult(SignOffStatus.InvalidState, $"The sign-off is {run.SignOffState}; reprocess the run."); }

        var blockers = await BlockersAsync(c, tx, runId, run, strict: false, ct);
        if (blockers.Count > 0) { return new SignOffResult(SignOffStatus.Blocked, "Sign-off is blocked: " + Join(blockers)); }

        string? approverName = null;
        if (requiredApproverId is not null)
        {
            if (requiredApproverId == ctx.ActorUserId) { return new SignOffResult(SignOffStatus.Invalid, "The required approver must be a different user than the preparer.", "/requiredApproverId"); }
            await using var u = Cmd(c, tx, """
                SELECT u.name FROM users u WHERE u.id=@a AND u.active
                  AND EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = u.id AND ur.role_id = 'APPROVER' AND ur.legal_entity_id IN (@le, '*'))
                """, ("a", requiredApproverId), ("le", run.LegalEntityId));
            approverName = await u.ExecuteScalarAsync(ct) as string;
            if (approverName is null) { return new SignOffResult(SignOffStatus.Invalid, "The required approver is not an active user with the APPROVER role in this legal entity.", "/requiredApproverId"); }
        }

        var states = await ExceptionStatesAsync(c, tx, runId, ct);
        var openHash = SignOffAttestation.OpenSetHash(states, IsOpen);
        var stateHash = SignOffAttestation.ExceptionStateHash(states);
        var pack = await LatestEvidencePackAsync(c, tx, runId, ct);

        long soId;
        await using (var ins = Cmd(c, tx, """
            INSERT INTO run_signoffs (run_id, state, prepared_by_user_id, required_approver_user_id, evidence_pack_id, submitted_at, submission_note, ruleset_version,
                                      submitted_result_hash, submitted_open_set_hash, submitted_exception_state_hash)
            VALUES (@r, 'APPROVAL_PENDING', @p, @a, @pack, now(), @note, @rs, @h, @oh, @sh) RETURNING id
            """, ("r", runId), ("p", ctx.ActorUserId), ("a", requiredApproverId), ("pack", pack), ("note", note), ("rs", run.RulesetVersion),
            ("h", run.ResultHash), ("oh", openHash), ("sh", stateHash)))
        {
            soId = (long)(await ins.ExecuteScalarAsync(ct))!;
        }

        var approvalId = "APR-" + Guid.NewGuid().ToString("N")[..16];
        await using (var ap = Cmd(c, tx, """
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, policy_version)
            VALUES (@id, 'RUN_SIGN_OFF', @r, @r, 1, @p, 'APPROVER', @pv)
            """, ("id", approvalId), ("r", runId), ("p", ctx.ActorUserId), ("pv", PolicyVersion)))
        {
            await ap.ExecuteNonQueryAsync(ct);
        }
        await using (var link = Cmd(c, tx, "UPDATE run_signoffs SET approval_id=@a WHERE id=@id", ("a", approvalId), ("id", soId))) { await link.ExecuteNonQueryAsync(ct); }
        await using (var upd = Cmd(c, tx, "UPDATE runs SET state='READY_FOR_SIGN_OFF', sign_off_state='APPROVAL_PENDING' WHERE id=@id AND version=@v", ("id", runId), ("v", run.Version)))
        {
            if (await upd.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The locked run row was not updated."); }
        }

        await audit.AppendAsync(tx, new AuditEventInput("SIGN_OFF_SUBMITTED", await ActorAsync(c, tx, ctx.ActorUserId, ct), "SIGN_OFF", runId,
            $"Run {runId} submitted for sign-off" + (approverName is null ? "" : $"; required approver {approverName}"),
            note is { Length: >= 3 } ? note : null, runId, run.RulesetVersion, ctx.RequestId,
            new JsonObject { ["state"] = run.State, ["signOffState"] = run.SignOffState }.ToJsonString(),
            new JsonObject { ["state"] = "READY_FOR_SIGN_OFF", ["signOffState"] = "APPROVAL_PENDING", ["preparerId"] = ctx.ActorUserId, ["requiredApproverId"] = requiredApproverId,
                ["approvalId"] = approvalId, ["resultHash"] = run.ResultHash, ["exceptionStateHash"] = stateHash }.ToJsonString(), run.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return SignOffResult.Ok;
    }

    public async Task<SignOffResult> ReturnAsync(string runId, string reason, SignOffCommandContext ctx, CancellationToken ct = default)
    {
        if (reason.Trim().Length < 3) { return new SignOffResult(SignOffStatus.Invalid, "A reason of at least 3 characters is required.", "/reason"); }
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var run = await LockRunAsync(c, tx, runId, ct);
        if (run is null) { return new SignOffResult(SignOffStatus.NotFound); }
        var so = await LatestSignOffAsync(c, tx, runId, true, ct);
        if (VersionsDiffer(run, so, ctx)) { return VersionMismatch(); }
        if (run.State == "SUPERSEDED") { return new SignOffResult(SignOffStatus.RunSuperseded, "The run is superseded; its sign-off is read-only."); }
        if (so is null || run.SignOffState != "APPROVAL_PENDING" || so.State != "APPROVAL_PENDING")
        {
            return new SignOffResult(SignOffStatus.InvalidState, $"Only a run waiting for approval can be returned; the sign-off is {run.SignOffState}.");
        }
        if (so.PreparedByUserId == ctx.ActorUserId) { return new SignOffResult(SignOffStatus.SeparationOfDuties, "The preparer cannot decide their own submission."); }
        if (so.RequiredApproverUserId is not null && so.RequiredApproverUserId != ctx.ActorUserId) { return new SignOffResult(SignOffStatus.Forbidden, "Only the required approver named at submission may return this run."); }

        await using (var upd = Cmd(c, tx, "UPDATE run_signoffs SET state='RETURNED', returned_reason=@r WHERE id=@id AND version=@v", ("r", reason.Trim()), ("id", so.Id), ("v", so.Version)))
        {
            if (await upd.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The locked sign-off row was not updated."); }
        }
        await DecideApprovalAsync(c, tx, so.ApprovalId, "REJECTED", "REJECT", ctx.ActorUserId, reason.Trim(), ct);
        await using (var upd = Cmd(c, tx, "UPDATE runs SET state='REVIEW_REQUIRED', sign_off_state='RETURNED' WHERE id=@id AND version=@v", ("id", runId), ("v", run.Version)))
        {
            if (await upd.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The locked run row was not updated."); }
        }
        await audit.AppendAsync(tx, new AuditEventInput("SIGN_OFF_RETURNED", await ActorAsync(c, tx, ctx.ActorUserId, ct), "SIGN_OFF", runId,
            $"Run {runId} returned to the preparer for further investigation", reason.Trim(), runId, run.RulesetVersion, ctx.RequestId,
            new JsonObject { ["state"] = run.State, ["signOffState"] = "APPROVAL_PENDING" }.ToJsonString(),
            new JsonObject { ["state"] = "REVIEW_REQUIRED", ["signOffState"] = "RETURNED", ["approvalId"] = so.ApprovalId }.ToJsonString(), run.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return SignOffResult.Ok;
    }

    private static async Task DecideApprovalAsync(NpgsqlConnection c, NpgsqlTransaction tx, string? approvalId, string status, string? decision, string? approverId, string? rationale, CancellationToken ct)
    {
        if (approvalId is null) { return; }
        await using var cmd = Cmd(c, tx, """
            UPDATE approvals SET status=@s, decision=@d, approver_user_id=@a, rationale=@r, decided_at=now() WHERE id=@id AND status='PENDING'
            """, ("s", status), ("d", decision), ("a", approverId), ("r", rationale), ("id", approvalId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SignOffResult> ApproveAsync(string runId, string reason, string? attestationTextHash, SignOffCommandContext ctx, CancellationToken ct = default)
    {
        if (reason.Trim().Length < 3) { return new SignOffResult(SignOffStatus.Invalid, "A reason of at least 3 characters is required.", "/reason"); }
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var run = await LockRunAsync(c, tx, runId, ct);
        if (run is null) { return new SignOffResult(SignOffStatus.NotFound); }
        var so = await LatestSignOffAsync(c, tx, runId, true, ct);
        if (VersionsDiffer(run, so, ctx)) { return VersionMismatch(); }
        if (run.State == "SUPERSEDED") { return new SignOffResult(SignOffStatus.RunSuperseded, "The run is superseded; its sign-off is read-only."); }
        if (so is null || run.SignOffState != "APPROVAL_PENDING" || so.State != "APPROVAL_PENDING")
        {
            return new SignOffResult(SignOffStatus.InvalidState, run.SignOffState == "SIGNED_OFF"
                ? "The run is already signed off."
                : $"The run must be submitted for sign-off first; the sign-off is {run.SignOffState}.");
        }
        if (so.PreparedByUserId == ctx.ActorUserId) { return new SignOffResult(SignOffStatus.SeparationOfDuties, "The preparer cannot sign off their own submission."); }
        if (so.RequiredApproverUserId is not null && so.RequiredApproverUserId != ctx.ActorUserId) { return new SignOffResult(SignOffStatus.Forbidden, "Only the required approver named at submission may sign off this run."); }

        // Stale detection: the result, the open-exception set or any exception state differs from what was submitted.
        var states = await ExceptionStatesAsync(c, tx, runId, ct);
        var openHash = SignOffAttestation.OpenSetHash(states, IsOpen);
        var stateHash = SignOffAttestation.ExceptionStateHash(states);
        var changes = new List<string>();
        if (so.SubmittedResultHash != run.ResultHash) { changes.Add("the run result changed"); }
        if (so.SubmittedOpenSetHash != openHash) { changes.Add("the set of open exceptions changed"); }
        if (so.SubmittedExceptionStateHash != stateHash) { changes.Add("an exception changed state"); }
        if (changes.Count > 0)
        {
            var detail = "Since submission " + string.Join(", ", changes) + "; the sign-off returned to PREPARATION_INCOMPLETE and must be prepared and submitted again.";
            await using (var upd = Cmd(c, tx, "UPDATE run_signoffs SET state='PREPARATION_INCOMPLETE', returned_reason=@r WHERE id=@id AND version=@v", ("r", detail), ("id", so.Id), ("v", so.Version))) { await upd.ExecuteNonQueryAsync(ct); }
            await DecideApprovalAsync(c, tx, so.ApprovalId, "INVALIDATED", null, null, detail, ct);
            await using (var upd = Cmd(c, tx, "UPDATE runs SET state='REVIEW_REQUIRED', sign_off_state='PREPARATION_INCOMPLETE' WHERE id=@id AND version=@v", ("id", runId), ("v", run.Version))) { await upd.ExecuteNonQueryAsync(ct); }
            await audit.AppendAsync(tx, new AuditEventInput("SIGN_OFF_INVALIDATED", await ActorAsync(c, tx, ctx.ActorUserId, ct), "SIGN_OFF", runId,
                $"Sign-off attempt for run {runId} refused as stale; sign-off returned to PREPARATION_INCOMPLETE", detail, runId, run.RulesetVersion, ctx.RequestId,
                new JsonObject { ["state"] = run.State, ["signOffState"] = "APPROVAL_PENDING" }.ToJsonString(),
                new JsonObject { ["state"] = "REVIEW_REQUIRED", ["signOffState"] = "PREPARATION_INCOMPLETE", ["stale"] = true, ["approvalId"] = so.ApprovalId }.ToJsonString(), run.LegalEntityId), ct);
            await tx.CommitAsync(ct);
            return new SignOffResult(SignOffStatus.StaleSignOff, detail);
        }

        var blockers = await BlockersAsync(c, tx, runId, run, strict: true, ct);
        if (blockers.Count > 0) { return new SignOffResult(SignOffStatus.Blocked, "Sign-off is blocked: " + Join(blockers)); }

        var qualifications = new List<string>();
        await using (var q = Cmd(c, tx, "SELECT control, finding_count FROM run_control_results WHERE run_id=@r AND state='QUALIFIED' ORDER BY control", ("r", runId)))
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) { qualifications.Add($"{r.GetString(0)} is qualified ({r.GetInt32(1)} findings)"); }
        }
        var text = SignOffAttestation.Text(runId, run.LegalEntityId, run.BusinessDate, run.RulesetVersion, run.ResultHash, qualifications);
        var textHash = SignOffAttestation.Sha256Hex(text);
        if (attestationTextHash is not null && !string.Equals(attestationTextHash, textHash, StringComparison.OrdinalIgnoreCase))
        {
            return new SignOffResult(SignOffStatus.StaleSignOff, "The attestation text changed since it was shown; re-read the sign-off pack.");
        }
        var attestationHash = SignOffAttestation.AttestationHash(textHash, run.ResultHash, stateHash, run.RulesetVersion);
        var pack = so.EvidencePackId ?? await LatestEvidencePackAsync(c, tx, runId, ct);

        await using (var upd = Cmd(c, tx, """
            UPDATE run_signoffs SET state='SIGNED_OFF', signed_by_user_id=@u, signed_at=now(), attestation_text=@t, attestation_text_hash=@th, attestation_hash=@ah,
                   signed_result_hash=@rh, signed_exception_state_hash=@sh, ruleset_version=@rs, evidence_pack_id=@pack
            WHERE id=@id AND version=@v
            """, ("u", ctx.ActorUserId), ("t", text), ("th", textHash), ("ah", attestationHash), ("rh", run.ResultHash), ("sh", stateHash), ("rs", run.RulesetVersion),
            ("pack", pack), ("id", so.Id), ("v", so.Version)))
        {
            if (await upd.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The locked sign-off row was not updated."); }
        }
        await DecideApprovalAsync(c, tx, so.ApprovalId, "APPROVED", "APPROVE", ctx.ActorUserId, reason.Trim(), ct);
        await using (var upd = Cmd(c, tx, "UPDATE runs SET state='SIGNED_OFF', sign_off_state='SIGNED_OFF' WHERE id=@id AND version=@v", ("id", runId), ("v", run.Version)))
        {
            if (await upd.ExecuteNonQueryAsync(ct) != 1) { throw new InvalidOperationException("The locked run row was not updated."); }
        }
        await audit.AppendAsync(tx, new AuditEventInput("RUN_SIGNED_OFF", await ActorAsync(c, tx, ctx.ActorUserId, ct), "SIGN_OFF", runId,
            $"Run {runId} signed off", reason.Trim(), runId, run.RulesetVersion, ctx.RequestId,
            new JsonObject { ["state"] = run.State, ["signOffState"] = "APPROVAL_PENDING" }.ToJsonString(),
            new JsonObject { ["state"] = "SIGNED_OFF", ["signOffState"] = "SIGNED_OFF", ["preparerId"] = so.PreparedByUserId, ["attestationHash"] = attestationHash,
                ["attestationTextHash"] = textHash, ["resultHash"] = run.ResultHash, ["exceptionStateHash"] = stateHash, ["rulesetVersion"] = run.RulesetVersion,
                ["approvalId"] = so.ApprovalId, ["evidencePackId"] = pack }.ToJsonString(), run.LegalEntityId), ct);
        await tx.CommitAsync(ct);
        return SignOffResult.Ok;
    }
}
