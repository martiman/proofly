using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Governance;

public interface ISettingsRepository
{
    /// <summary>Null when the legal entity does not exist or is outside <paramref name="scope"/> (null scope = all). Without an id the first in-scope entity is used.</summary>
    Task<SettingsSnapshot?> GetAsync(string? legalEntityId, IReadOnlyCollection<string>? scope, CancellationToken ct = default);
}

public interface IApprovalRepository
{
    Task<ApprovalPageRows> ListAsync(ApprovalQuery query, CancellationToken ct = default);
}

public interface IEvidencePackRepository
{
    /// <summary>Null when the run does not exist or is out of scope (null scope = all).</summary>
    Task<EvidencePackRow?> GetForRunAsync(string runId, IReadOnlyCollection<string>? scope, CancellationToken ct = default);
}

public static class GovernanceDataModule
{
    public static IServiceCollection AddGovernanceData(this IServiceCollection s)
    {
        s.AddSingleton<ISettingsRepository, SettingsRepository>();
        s.AddSingleton<IApprovalRepository, ApprovalRepository>();
        s.AddSingleton<IEvidencePackRepository, EvidencePackRepository>();
        return s;
    }
}

internal static class Sql
{
    public static NpgsqlParameter TextArray(string name, IEnumerable<string>? v) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = v?.ToArray() ?? (object)DBNull.Value };

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    public static bool Bool(this JsonElement e, string name, bool dflt) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : dflt;
}

public sealed class SettingsRepository(NpgsqlDataSource ds) : ISettingsRepository
{
    public async Task<SettingsSnapshot?> GetAsync(string? legalEntityId, IReadOnlyCollection<string>? scope, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        LegalEntityRow? le = null;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, name, ARRAY(SELECT c FROM unnest(currencies) AS c ORDER BY c) FROM legal_entities
            WHERE active AND (@id::text IS NULL OR id = @id) AND (@scope::text[] IS NULL OR id = ANY(@scope))
            ORDER BY id LIMIT 1
            """, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = (object?)legalEntityId ?? DBNull.Value });
            cmd.Parameters.Add(Sql.TextArray("scope", scope));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { le = new(r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2)); }
        }
        if (le is null) { return null; }

        // Pending changes: config changes are keyed by the config row, access changes by the user (the oldest pending change wins per target).
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingUsers = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingCount = 0;
        await using (var cmd = new NpgsqlCommand("SELECT id, config_id, target_user_id FROM config_changes WHERE status = 'PENDING_APPROVAL' ORDER BY created_at, id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                pendingCount++;
                var target = r.IsDBNull(1) ? r.GetString(2) : r.GetString(1);
                (r.IsDBNull(1) ? pendingUsers : pending).TryAdd(target, r.GetString(0));
            }
        }

        var rules = new List<RuleConfigRow>(); var sources = new List<SourceConfigRow>(); var policies = new List<ApprovalPolicyRow>();
        var freshness = "";
        await using (var cmd = new NpgsqlCommand("SELECT id, kind, name, config_version, payload::text FROM config_rules WHERE enabled ORDER BY kind, id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0); var kind = r.GetString(1); var name = r.GetString(2); var ver = r.GetString(3);
                using var doc = JsonDocument.Parse(r.GetString(4)); var p = doc.RootElement;
                pending.TryGetValue(id, out var pc);
                switch (kind)
                {
                    case "rule":
                        rules.Add(new(id, p.Str("control") ?? "", ver, p.Str("description") ?? name, p.Str("tolerance") ?? "", p.Str("severity") ?? "", p.Str("effectiveFrom") ?? "", pc));
                        break;
                    case "source":
                        sources.Add(new(name, p.Str("schemaVersion") ?? ver, p.Str("expectedByLocalTime") ?? "", p.Bool("mandatory", true), pc));
                        break;
                    case "approval_policy":
                        policies.Add(new(name, p.Str("description") ?? "", p.Str("requiredRole"), ver));
                        break;
                    case "setting" when id == "freshness_policy":
                        freshness = p.Str("text") ?? "";
                        break;
                }
            }
        }

        var users = new List<UserAccessRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT u.id, u.name, u.email,
                   array_agg(DISTINCT ur.role_id ORDER BY ur.role_id) FILTER (WHERE ur.legal_entity_id IN ('*', @le)),
                   array_agg(DISTINCT ur.legal_entity_id ORDER BY ur.legal_entity_id)
            FROM users u JOIN user_roles ur ON ur.user_id = u.id
            WHERE u.active
            GROUP BY u.id, u.name, u.email
            HAVING bool_or(ur.legal_entity_id IN ('*', @le))
            ORDER BY u.name, u.id
            """, conn))
        {
            cmd.Parameters.AddWithValue("le", le.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { users.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<string[]>(4), pendingUsers.GetValueOrDefault(r.GetString(0)))); }
        }

        return new SettingsSnapshot(le, rules, sources, users, freshness, policies, pendingCount);
    }
}

public sealed class ApprovalRepository(NpgsqlDataSource ds) : IApprovalRepository
{
    public async Task<ApprovalPageRows> ListAsync(ApprovalQuery q, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            WITH visible AS (
              SELECT a.*, r.legal_entity_id AS le FROM approvals a LEFT JOIN runs r ON r.id = a.run_id
              WHERE (a.run_id IS NULL OR @scope::text[] IS NULL OR r.legal_entity_id = ANY(@scope))
            ), x AS (
              SELECT v.*,
                (v.status = 'PENDING' AND v.proposer_user_id <> @me AND EXISTS (
                   SELECT 1 FROM user_roles ur WHERE ur.user_id = @me AND ur.role_id = v.required_role
                     AND (ur.legal_entity_id = '*' OR v.le IS NULL OR ur.legal_entity_id = v.le))) AS can_decide
              FROM visible v
            )
            SELECT x.id, x.object_type, x.object_id, x.run_id, x.proposal_version, x.proposer_user_id, p.name, x.approver_user_id, ap.name,
                   x.required_role, x.status, x.decision, x.rationale, x.evidence_refs::text, x.policy_version, x.proposed_at, x.decided_at, x.can_decide,
                   (SELECT rp.kind FROM resolution_proposals rp WHERE x.object_type = 'EXCEPTION_RESOLUTION' AND rp.id = x.object_id)
            FROM x JOIN users p ON p.id = x.proposer_user_id LEFT JOIN users ap ON ap.id = x.approver_user_id
            WHERE (@type::text IS NULL OR x.object_type = @type)
              AND (NOT @mine OR x.can_decide)
              AND (@cat::timestamptz IS NULL OR (x.proposed_at, x.id) < (@cat, @cid))
            ORDER BY x.proposed_at DESC, x.id DESC LIMIT @lim
            """, conn);
        cmd.Parameters.AddWithValue("me", q.CallerId);
        cmd.Parameters.Add(Sql.TextArray("scope", q.LegalEntityIds));
        cmd.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = (object?)q.ObjectType ?? DBNull.Value });
        cmd.Parameters.AddWithValue("mine", q.Mine);
        cmd.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.TimestampTz) { Value = q.After is { } a ? a.At : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cid", NpgsqlDbType.Text) { Value = (object?)q.After?.Id ?? "" });
        cmd.Parameters.AddWithValue("lim", q.Limit + 1);

        var items = new List<ApprovalRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var refs = JsonSerializer.Deserialize<string[]>(r.GetString(13)) ?? [];
            items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4),
                new(r.GetString(5), r.GetString(6)), r.IsDBNull(7) ? null : new(r.GetString(7), r.GetString(8)),
                r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), refs,
                r.GetString(14), r.GetFieldValue<DateTimeOffset>(15), r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16), r.GetBoolean(17),
                r.IsDBNull(18) ? null : r.GetString(18)));
        }
        var more = items.Count > q.Limit;
        if (more) { items.RemoveAt(items.Count - 1); }
        return new ApprovalPageRows(items, more);
    }
}

public sealed class EvidencePackRepository(NpgsqlDataSource ds) : IEvidencePackRepository
{
    /// <summary>Job type #46 enqueues for generation; used only to report GENERATING / FAILED while no pack row exists.</summary>
    public const string GenerateJobType = "evidence_pack.generate";

    public async Task<EvidencePackRow?> GetForRunAsync(string runId, IReadOnlyCollection<string>? scope, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM runs WHERE id = @r AND (@scope::text[] IS NULL OR legal_entity_id = ANY(@scope))", conn))
        {
            cmd.Parameters.AddWithValue("r", runId);
            cmd.Parameters.Add(Sql.TextArray("scope", scope));
            if (await cmd.ExecuteScalarAsync(ct) is null) { return null; }
        }

        await using (var cmd = new NpgsqlCommand("""
            SELECT id, status, sha256, size_bytes, contents::text, failure_reason, created_at, manifest::text, audit_chain::text
            FROM evidence_packs WHERE run_id = @r ORDER BY created_at DESC, id DESC LIMIT 1
            """, conn))
        {
            cmd.Parameters.AddWithValue("r", runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var status = r.GetString(1) switch { "READY" => "GENERATED", "GENERATING" => "GENERATING", _ => "FAILED" };
                var contents = JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [];
                return new(r.GetString(0), runId, status == "GENERATED" ? r.GetFieldValue<DateTimeOffset>(6) : null, status,
                    r.IsDBNull(2) ? null : r.GetString(2).Trim(), r.IsDBNull(3) ? null : r.GetInt64(3), contents, r.IsDBNull(5) ? null : r.GetString(5),
                    status == "GENERATED" && !r.IsDBNull(7) ? r.GetString(7) : null, status == "GENERATED" && !r.IsDBNull(8) ? r.GetString(8) : null);
            }
        }
        // No pack row yet: report the generation job (if one was enqueued) so the client can tell GENERATING / FAILED from NOT_GENERATED.
        await using (var cmd = new NpgsqlCommand("SELECT state FROM jobs WHERE type = @t AND run_id = @r ORDER BY id DESC LIMIT 1", conn))
        {
            cmd.Parameters.AddWithValue("t", GenerateJobType);
            cmd.Parameters.AddWithValue("r", runId);
            var state = await cmd.ExecuteScalarAsync(ct) as string;
            var status = state switch { "QUEUED" or "RUNNING" or "FAILED" => "GENERATING", "DEAD" => "FAILED", _ => "NOT_GENERATED" };
            return new(null, runId, null, status, null, null, [], null);
        }
    }
}
