using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Proofly.Data.Audit;
using Proofly.Domain.Enums;

namespace Proofly.Data.Governance.Commands;

public interface IConfigChangeRepository
{
    Task<ConfigChangePageRows> ListAsync(ConfigChangeQuery query, CancellationToken ct = default);
    Task<ConfigChangeRow?> GetAsync(string id, CancellationToken ct = default);
    Task<ConfigChangeRow> ProposeAsync(ProposeConfigChangeCommand cmd, CancellationToken ct = default);
    Task<DecisionResult> DecideAsync(DecideConfigChangeCommand cmd, CancellationToken ct = default);
}

/// <summary>
/// Ruleset in force at a point in time. Orchestration (run start, #39) calls this inside its own transaction and stores
/// <see cref="RulesetVersion.Version"/> in runs.ruleset_version. A change approved after a run started never alters that run.
/// </summary>
public interface IRulesetResolver
{
    /// <summary>The latest ruleset activated at or before <paramref name="asOf"/> whose effective date (UTC) has been reached; never null (seed 'v1').</summary>
    Task<RulesetVersion> ResolveAsync(DateTimeOffset asOf, NpgsqlTransaction? tx = null, CancellationToken ct = default);
}

public static class GovernanceCommandsDataModule
{
    public static IServiceCollection AddGovernanceCommandsData(this IServiceCollection s)
    {
        s.AddSingleton<IConfigChangeRepository, ConfigChangeRepository>();
        s.AddSingleton<IRulesetResolver, RulesetResolver>();
        return s;
    }
}

public sealed class RulesetResolver(NpgsqlDataSource ds) : IRulesetResolver
{
    public async Task<RulesetVersion> ResolveAsync(DateTimeOffset asOf, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var lease = tx is null ? await ds.OpenConnectionAsync(ct) : null;
        await using var cmd = new NpgsqlCommand(
            """
            SELECT version, activated_at, effective_from, change_id, snapshot::text FROM ruleset_versions
            WHERE activated_at <= @t AND effective_from <= @d ORDER BY seq DESC LIMIT 1
            """, tx?.Connection ?? lease, tx);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.TimestampTz) { Value = asOf.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = DateOnly.FromDateTime(asOf.UtcDateTime) });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) { throw new InvalidOperationException("No ruleset version is active at the requested time."); }
        return new(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4));
    }
}

public sealed class ConfigChangeRepository(NpgsqlDataSource ds, IAuditWriter audit, TimeProvider time) : IConfigChangeRepository
{
    private const string PolicyVersion = "adr-0005";

    private const string Select =
        """
        SELECT c.id, c.change_kind, c.target_kind, c.target_id, c.status, c.reason, c.before::text, c.after::text, c.effective_from,
               c.proposed_by_user_id, p.name, c.created_at, c.version, c.base_version, c.ruleset_version,
               a.id, a.proposal_version, a.approver_user_id, ap.name, a.required_role, a.status, a.decision, a.rationale, a.policy_version, a.proposed_at, a.decided_at
        FROM config_changes c
        JOIN users p ON p.id = c.proposed_by_user_id
        LEFT JOIN approvals a ON a.id = c.approval_id
        LEFT JOIN users ap ON ap.id = a.approver_user_id
        """;

    private static ConfigChangeRow Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), DateOnly.FromDateTime(r.GetDateTime(8)),
        r.GetString(9), r.GetString(10), r.GetFieldValue<DateTimeOffset>(11), r.GetInt32(12), r.IsDBNull(13) ? null : r.GetInt32(13),
        r.IsDBNull(15) ? null : new ApprovalInfo(r.GetString(15), r.GetInt32(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18),
            r.GetString(19), r.GetString(20), r.IsDBNull(21) ? null : r.GetString(21), r.IsDBNull(22) ? null : r.GetString(22), r.GetString(23),
            r.GetFieldValue<DateTimeOffset>(24), r.IsDBNull(25) ? null : r.GetFieldValue<DateTimeOffset>(25)),
        r.IsDBNull(14) ? null : r.GetString(14));

    public async Task<ConfigChangePageRows> ListAsync(ConfigChangeQuery q, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(Select +
            """

            WHERE (@st::text IS NULL OR c.status = @st) AND (@tk::text IS NULL OR c.target_kind = @tk)
              AND (@cat::timestamptz IS NULL OR (c.created_at, c.id) < (@cat, @cid))
            ORDER BY c.created_at DESC, c.id DESC LIMIT @lim
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = (object?)q.Status ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tk", NpgsqlDbType.Text) { Value = (object?)q.TargetKind ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.TimestampTz) { Value = q.After is { } a ? a.At : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cid", NpgsqlDbType.Text) { Value = (object?)q.After?.Id ?? "" });
        cmd.Parameters.AddWithValue("lim", q.Limit + 1);
        var items = new List<ConfigChangeRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { items.Add(Read(r)); }
        var more = items.Count > q.Limit;
        if (more) { items.RemoveAt(items.Count - 1); }
        return new(items, more);
    }

    public async Task<ConfigChangeRow?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await LoadAsync(conn, null, id, false, ct);
    }

    private static async Task<ConfigChangeRow?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string id, bool lockRow, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(Select + " WHERE c.id = @id" + (lockRow ? " FOR UPDATE OF c" : ""), conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static string Fail(GovernanceFailure f, string detail, string? pointer = null) => throw new GovernanceCommandException(f, detail, pointer);

    private static async Task<string> ActorNameAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT name FROM users WHERE id = @id AND active", conn, tx);
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteScalarAsync(ct) as string ?? Fail(GovernanceFailure.Forbidden, "The acting user is unknown or inactive.");
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>Current access of a user as the mutable-key object {legalEntityIds, roles}; null when the user does not exist or is inactive.</summary>
    private static async Task<JsonObject?> UserAccessAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using (var u = new NpgsqlCommand("SELECT 1 FROM users WHERE id = @id AND active", conn, tx))
        {
            u.Parameters.AddWithValue("id", userId);
            if (await u.ExecuteScalarAsync(ct) is null) { return null; }
        }
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(array_agg(DISTINCT role_id ORDER BY role_id), '{}'), COALESCE(array_agg(DISTINCT legal_entity_id ORDER BY legal_entity_id), '{}')
            FROM user_roles WHERE user_id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new JsonObject
        {
            ["legalEntityIds"] = new JsonArray(r.GetFieldValue<string[]>(1).Select(x => (JsonNode)x).ToArray()),
            ["roles"] = new JsonArray(r.GetFieldValue<string[]>(0).Select(x => (JsonNode)x).ToArray()),
        };
    }

    public async Task<ConfigChangeRow> ProposeAsync(ProposeConfigChangeCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Reason.Trim().Length < 3) { Fail(GovernanceFailure.Validation, "A reason of at least 3 characters is required.", "/reason"); }
        if (!ConfigTargetKinds.All.Contains(cmd.TargetKind)) { Fail(GovernanceFailure.Validation, "Unknown target kind.", "/targetKind"); }
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (cmd.EffectiveFrom < today) { Fail(GovernanceFailure.Validation, "effectiveFrom must not be in the past.", "/effectiveFrom"); }
        var (value, errors) = ConfigPayloadValidator.Validate(cmd.TargetKind, cmd.TargetId, cmd.After);
        if (value is null) { throw new GovernanceCommandException(GovernanceFailure.Validation, errors[0].Message, errors[0].Pointer); }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var actorName = await ActorNameAsync(conn, tx, cmd.ActorId, ct);

        var isAccess = cmd.TargetKind == ConfigTargetKinds.UserAccess;
        string? configId = null; int? baseVersion = null; JsonObject? before;
        string targetId = cmd.TargetId;
        if (isAccess)
        {
            before = await UserAccessAsync(conn, tx, cmd.TargetId, ct) ?? Fail<JsonObject>(GovernanceFailure.Validation, "Unknown or inactive user.", "/targetId");
            var known = new HashSet<string>(StringComparer.Ordinal) { "*" };
            await using (var le = new NpgsqlCommand("SELECT id FROM legal_entities", conn, tx))
            await using (var r = await le.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) { known.Add(r.GetString(0)); } }
            foreach (var id in value["legalEntityIds"]!.AsArray().Select(n => n!.GetValue<string>()))
            {
                if (!known.Contains(id)) { Fail(GovernanceFailure.Validation, $"Unknown legal entity '{id}'.", "/after/legalEntityIds"); }
            }
        }
        else
        {
            var kind = cmd.TargetKind switch { ConfigTargetKinds.Rule => "rule", ConfigTargetKinds.Source => "source", _ => "approval_policy" };
            await using var c = new NpgsqlCommand("SELECT id, payload::text, version FROM config_rules WHERE enabled AND kind = @k AND (id = @t OR name = @t) ORDER BY (id = @t) DESC LIMIT 1", conn, tx);
            c.Parameters.AddWithValue("k", kind); c.Parameters.AddWithValue("t", cmd.TargetId);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { Fail(GovernanceFailure.Validation, "Unknown target.", "/targetId"); }
            configId = r.GetString(0); before = (JsonObject)JsonNode.Parse(r.GetString(1))!; baseVersion = r.GetInt32(2);
        }
        if (JsonNode.DeepEquals(Canon(before), Canon(value))) { Fail(GovernanceFailure.Validation, "The proposed object equals the current one.", "/after"); }

        var changeId = NewId("CC-"); var approvalId = NewId("APR-");
        var requiredRole = isAccess ? "SECURITY_ADMIN" : "CONFIG_ADMIN";
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO approvals (id, object_type, object_id, proposal_version, proposer_user_id, required_role, policy_version)
            VALUES (@aid, @ot, @cid, 1, @me, @role, @pv)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("aid", approvalId); ins.Parameters.AddWithValue("ot", isAccess ? "ACCESS_CHANGE" : "CONFIG_CHANGE");
            ins.Parameters.AddWithValue("cid", changeId); ins.Parameters.AddWithValue("me", cmd.ActorId);
            ins.Parameters.AddWithValue("role", requiredRole); ins.Parameters.AddWithValue("pv", PolicyVersion);
            await ins.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var ins = new NpgsqlCommand(
                """
                INSERT INTO config_changes (id, change_kind, config_id, target_user_id, target_kind, target_id, proposed_by_user_id, reason, before, after,
                                            status, approval_id, effective_from, base_version)
                VALUES (@id, @ck, @cfg, @usr, @tk, @tid, @me, @reason, @before, @after, 'PENDING_APPROVAL', @aid, @ef, @bv)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", changeId); ins.Parameters.AddWithValue("ck", isAccess ? "ACCESS" : "CONFIG");
            ins.Parameters.Add(new NpgsqlParameter("cfg", NpgsqlDbType.Text) { Value = (object?)configId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("usr", NpgsqlDbType.Text) { Value = isAccess ? targetId : DBNull.Value });
            ins.Parameters.AddWithValue("tk", cmd.TargetKind); ins.Parameters.AddWithValue("tid", isAccess ? targetId : configId!);
            ins.Parameters.AddWithValue("me", cmd.ActorId); ins.Parameters.AddWithValue("reason", cmd.Reason.Trim());
            ins.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb) { Value = before.ToJsonString() });
            ins.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb) { Value = value.ToJsonString() });
            ins.Parameters.AddWithValue("aid", approvalId); ins.Parameters.Add(new NpgsqlParameter("ef", NpgsqlDbType.Date) { Value = cmd.EffectiveFrom });
            ins.Parameters.Add(new NpgsqlParameter("bv", NpgsqlDbType.Integer) { Value = (object?)baseVersion ?? DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            Fail(GovernanceFailure.Duplicate, "A change for this target is already awaiting approval.");
        }

        await audit.AppendAsync(tx, new AuditEventInput("CONFIG_CHANGE_PROPOSED", new AuditActor(ActorType.User, cmd.ActorId, actorName), "CONFIG", changeId,
            $"Proposed {(isAccess ? "access" : "configuration")} change to {cmd.TargetKind} {targetId}, effective from {cmd.EffectiveFrom:yyyy-MM-dd}.",
            cmd.Reason.Trim(), RequestId: cmd.RequestId, BeforeJson: before.ToJsonString(), AfterJson: value.ToJsonString()), ct);
        var row = await LoadAsync(conn, tx, changeId, false, ct);
        await tx.CommitAsync(ct);
        return row!;
    }

    private static T Fail<T>(GovernanceFailure f, string detail, string? pointer = null) => throw new GovernanceCommandException(f, detail, pointer);

    /// <summary>Order-insensitive comparison form (arrays of strings are already sorted by the validator and by the access query).</summary>
    private static JsonNode Canon(JsonObject o) => JsonNode.Parse(o.ToJsonString())!;

    public async Task<DecisionResult> DecideAsync(DecideConfigChangeCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Reason.Trim().Length < 3) { Fail(GovernanceFailure.Validation, "A reason of at least 3 characters is required.", "/reason"); }
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var change = await LoadAsync(conn, tx, cmd.ChangeId, true, ct) ?? Fail<ConfigChangeRow>(GovernanceFailure.NotFound, "Configuration change not found.");
        cmd.Authorize(change); // scope, permission, If-Match (version of the locked row)
        var actorName = await ActorNameAsync(conn, tx, cmd.ActorId, ct);

        if (change.ProposerId == cmd.ActorId) { Fail(GovernanceFailure.SeparationOfDuties, "The proposer cannot decide their own change."); }
        if (change.Status != ConfigChangeStatuses.Pending) { Fail(GovernanceFailure.InvalidState, $"The change is {change.Status}; only a PENDING_APPROVAL change can be decided."); }
        if (cmd.ProposalVersion is { } pv && change.Approval is { } ap0 && pv != ap0.ProposalVersion)
        { Fail(GovernanceFailure.StaleProposal, "The proposal changed since it was reviewed (stale proposalVersion)."); }

        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        string? rulesetVersion = null; string newStatus;
        var after = change.After;
        DateOnly effective = change.EffectiveFrom;

        if (cmd.Approve)
        {
            newStatus = ConfigChangeStatuses.Active;
            if (change.IsAccess)
            {
                var current = await UserAccessAsync(conn, tx, change.TargetId, ct) ?? Fail<JsonObject>(GovernanceFailure.InvalidState, "The target user is no longer active.");
                var beforeJson = change.BeforeJson is null ? null : JsonNode.Parse(change.BeforeJson);
                if (!JsonNode.DeepEquals(beforeJson, Canon(current))) { Fail(GovernanceFailure.InvalidState, "The user's access changed since the proposal; propose again."); }
                await using (var del = new NpgsqlCommand("DELETE FROM user_roles WHERE user_id = @u", conn, tx)) { del.Parameters.AddWithValue("u", change.TargetId); await del.ExecuteNonQueryAsync(ct); }
                foreach (var role in after["roles"]!.AsArray().Select(n => n!.GetValue<string>()))
                foreach (var le in after["legalEntityIds"]!.AsArray().Select(n => n!.GetValue<string>()))
                {
                    await using var ins = new NpgsqlCommand("INSERT INTO user_roles (user_id, role_id, legal_entity_id) VALUES (@u, @r, @l)", conn, tx);
                    ins.Parameters.AddWithValue("u", change.TargetId); ins.Parameters.AddWithValue("r", role); ins.Parameters.AddWithValue("l", le);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await using (var touch = new NpgsqlCommand("UPDATE users SET updated_at = now() WHERE id = @u", conn, tx)) { touch.Parameters.AddWithValue("u", change.TargetId); await touch.ExecuteNonQueryAsync(ct); }
            }
            else
            {
                effective = effective < today ? today : effective;
                await using (var lk = new NpgsqlCommand("LOCK TABLE ruleset_versions IN EXCLUSIVE MODE", conn, tx)) { await lk.ExecuteNonQueryAsync(ct); }
                int seq;
                await using (var sq = new NpgsqlCommand("SELECT COALESCE(max(seq), 0) + 1 FROM ruleset_versions", conn, tx)) { seq = Convert.ToInt32(await sq.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
                rulesetVersion = "v" + seq.ToString(CultureInfo.InvariantCulture);
                await using (var up = new NpgsqlCommand("UPDATE config_rules SET payload = @p, config_version = @v WHERE id = @id AND version = @bv", conn, tx))
                {
                    up.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = change.AfterJson });
                    up.Parameters.AddWithValue("v", rulesetVersion); up.Parameters.AddWithValue("id", change.TargetId);
                    up.Parameters.Add(new NpgsqlParameter("bv", NpgsqlDbType.Integer) { Value = (object?)change.BaseVersion ?? DBNull.Value });
                    if (await up.ExecuteNonQueryAsync(ct) == 0) { Fail(GovernanceFailure.InvalidState, "The configuration changed since the proposal; propose again."); }
                }
                await using (var rs = new NpgsqlCommand(
                    """
                    INSERT INTO ruleset_versions (version, seq, activated_at, effective_from, change_id, snapshot)
                    SELECT @v, @s, @at, @ef, @cid, COALESCE(jsonb_agg(jsonb_build_object('id', id, 'kind', kind, 'name', name, 'version', config_version, 'payload', payload) ORDER BY id), '[]'::jsonb)
                    FROM config_rules WHERE enabled
                    """, conn, tx))
                {
                    rs.Parameters.AddWithValue("v", rulesetVersion); rs.Parameters.AddWithValue("s", seq);
                    rs.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = now });
                    rs.Parameters.Add(new NpgsqlParameter("ef", NpgsqlDbType.Date) { Value = effective }); rs.Parameters.AddWithValue("cid", change.Id);
                    await rs.ExecuteNonQueryAsync(ct);
                }
            }
            await using (var ret = new NpgsqlCommand(
                "UPDATE config_changes SET status = 'RETIRED' WHERE status = 'ACTIVE' AND target_kind = @tk AND target_id = @tid AND id <> @id", conn, tx))
            { ret.Parameters.AddWithValue("tk", change.TargetKind); ret.Parameters.AddWithValue("tid", change.TargetId); ret.Parameters.AddWithValue("id", change.Id); await ret.ExecuteNonQueryAsync(ct); }
        }
        else { newStatus = ConfigChangeStatuses.Rejected; }

        await using (var up = new NpgsqlCommand(
            """
            UPDATE config_changes SET status = @st, decided_by_user_id = @me, decided_at = @at, ruleset_version = @rv, effective_from = @ef
            WHERE id = @id AND version = @v AND status = 'PENDING_APPROVAL'
            """, conn, tx))
        {
            up.Parameters.AddWithValue("st", newStatus); up.Parameters.AddWithValue("me", cmd.ActorId);
            up.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = now });
            up.Parameters.Add(new NpgsqlParameter("rv", NpgsqlDbType.Text) { Value = (object?)rulesetVersion ?? DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("ef", NpgsqlDbType.Date) { Value = effective });
            up.Parameters.AddWithValue("id", change.Id); up.Parameters.AddWithValue("v", change.Version);
            if (await up.ExecuteNonQueryAsync(ct) == 0) { Fail(GovernanceFailure.InvalidState, "The change was modified concurrently."); }
        }
        if (change.Approval is { } ap)
        {
            await using var up = new NpgsqlCommand(
                "UPDATE approvals SET status = @st, decision = @d, approver_user_id = @me, rationale = @r, decided_at = @at WHERE id = @id AND status = 'PENDING'", conn, tx);
            up.Parameters.AddWithValue("st", cmd.Approve ? "APPROVED" : "REJECTED"); up.Parameters.AddWithValue("d", cmd.Approve ? "APPROVE" : "REJECT");
            up.Parameters.AddWithValue("me", cmd.ActorId); up.Parameters.AddWithValue("r", cmd.Reason.Trim());
            up.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = now }); up.Parameters.AddWithValue("id", ap.Id);
            if (await up.ExecuteNonQueryAsync(ct) == 0) { Fail(GovernanceFailure.InvalidState, "The approval was already decided."); }
        }

        var actor = new AuditActor(ActorType.User, cmd.ActorId, actorName);
        var afterAudit = new JsonObject { ["status"] = newStatus, ["decision"] = cmd.Approve ? "APPROVE" : "REJECT", ["effectiveFrom"] = effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (rulesetVersion is not null) { afterAudit["rulesetVersion"] = rulesetVersion; }
        await audit.AppendAsync(tx, new AuditEventInput("CONFIG_CHANGE_DECIDED", actor, "CONFIG", change.Id,
            $"{(cmd.Approve ? "Approved" : "Rejected")} {(change.IsAccess ? "access" : "configuration")} change to {change.TargetKind} {change.TargetId}.",
            cmd.Reason.Trim(), RuleVersion: rulesetVersion, RequestId: cmd.RequestId,
            BeforeJson: new JsonObject { ["status"] = change.Status }.ToJsonString(), AfterJson: afterAudit.ToJsonString()), ct);
        if (cmd.Approve && change.IsAccess)
        {
            await audit.AppendAsync(tx, new AuditEventInput("ACCESS_CHANGED", actor, "CONFIG", change.Id,
                $"Access of user {change.TargetId} changed.", cmd.Reason.Trim(), RequestId: cmd.RequestId, BeforeJson: change.BeforeJson, AfterJson: change.AfterJson), ct);
        }
        var row = await LoadAsync(conn, tx, change.Id, false, ct);
        await tx.CommitAsync(ct);
        return new DecisionResult(row!, rulesetVersion);
    }
}
