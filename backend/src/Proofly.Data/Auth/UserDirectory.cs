using Npgsql;

namespace Proofly.Data.Auth;

/// <summary>One role held within a legal-entity scope ('*' = all entities).</summary>
public sealed record RoleAssignment(string RoleId, string LegalEntityId);

public sealed record UserRecord(string Id, string Name, string Email, string? ExternalSubject, bool Active, IReadOnlyList<RoleAssignment> Assignments);

/// <summary>A material access decision (user provisioned). Written by the hook in the same transaction as the change.</summary>
public sealed record AuthAuditEvent(string EventType, string SubjectKind, string SubjectId, string Reason, string? After);

/// <summary>Writes the audit event inside the caller's transaction (implemented over the audit store, #16).</summary>
public interface IAuthAuditHook
{
    Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AuthAuditEvent evt, CancellationToken ct);
}

/// <summary>Reads (and, for local evaluation or opt-in provisioning, writes) users, roles and scope.</summary>
public interface IUserDirectory
{
    Task<UserRecord?> FindBySubjectAsync(string subject, CancellationToken ct = default);
    Task<UserRecord?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Creates a user with no roles for an identity-provider subject (first login). Returns null when the e-mail already
    /// belongs to another user. The audit hook runs in the same transaction; the user is not created if it fails.
    /// </summary>
    Task<UserRecord?> ProvisionAsync(string subject, string name, string email, IAuthAuditHook audit, CancellationToken ct = default);

    /// <summary>Idempotently creates or updates a fixed user and its assignments (local evaluation seeding only).</summary>
    Task EnsureUserAsync(UserRecord user, CancellationToken ct = default);
}

public sealed class UserDirectory(NpgsqlDataSource dataSource) : IUserDirectory
{
    private const string Select = """
        SELECT u.id, u.name, u.email, u.external_subject, u.active, ur.role_id, ur.legal_entity_id
        FROM users u LEFT JOIN user_roles ur ON ur.user_id = u.id
        """;

    public Task<UserRecord?> FindBySubjectAsync(string subject, CancellationToken ct = default) =>
        FindAsync(Select + " WHERE u.external_subject = @k ORDER BY ur.role_id, ur.legal_entity_id", subject, ct);

    public Task<UserRecord?> FindByIdAsync(string id, CancellationToken ct = default) =>
        FindAsync(Select + " WHERE u.id = @k ORDER BY ur.role_id, ur.legal_entity_id", id, ct);

    private async Task<UserRecord?> FindAsync(string sql, string key, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await ReadAsync(r, ct);
    }

    private static async Task<UserRecord?> ReadAsync(NpgsqlDataReader r, CancellationToken ct)
    {
        UserRecord? user = null;
        var assignments = new List<RoleAssignment>();
        while (await r.ReadAsync(ct))
        {
            user ??= new UserRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), assignments);
            if (!r.IsDBNull(5)) { assignments.Add(new RoleAssignment(r.GetString(5), r.GetString(6))); }
        }
        return user;
    }

    public async Task<UserRecord?> ProvisionAsync(string subject, string name, string email, IAuthAuditHook audit, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = "usr-" + Guid.NewGuid().ToString("N");
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO users (id, name, email, external_subject) VALUES (@id, @n, @e, @s) ON CONFLICT DO NOTHING", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("n", name);
            ins.Parameters.AddWithValue("e", email);
            ins.Parameters.AddWithValue("s", subject);
            if (await ins.ExecuteNonQueryAsync(ct) == 0) { return null; }
        }
        await audit.RecordAsync(conn, tx, new AuthAuditEvent("ACCESS_CHANGED", "USER", id,
            "First-login provisioning from the identity provider; no roles granted.", $"{{\"externalSubject\":\"{subject.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}}"), ct);
        await tx.CommitAsync(ct);
        return new UserRecord(id, name, email, subject, true, []);
    }

    public async Task EnsureUserAsync(UserRecord user, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var up = new NpgsqlCommand("""
            INSERT INTO users (id, name, email, external_subject, active) VALUES (@id, @n, @e, @s, @a)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, email = EXCLUDED.email,
              external_subject = EXCLUDED.external_subject, active = EXCLUDED.active
            """, conn, tx))
        {
            up.Parameters.AddWithValue("id", user.Id);
            up.Parameters.AddWithValue("n", user.Name);
            up.Parameters.AddWithValue("e", user.Email);
            up.Parameters.AddWithValue("s", (object?)user.ExternalSubject ?? DBNull.Value);
            up.Parameters.AddWithValue("a", user.Active);
            await up.ExecuteNonQueryAsync(ct);
        }
        foreach (var a in user.Assignments)
        {
            await using var ur = new NpgsqlCommand(
                "INSERT INTO user_roles (user_id, role_id, legal_entity_id) VALUES (@u, @r, @l) ON CONFLICT DO NOTHING", conn, tx);
            ur.Parameters.AddWithValue("u", user.Id);
            ur.Parameters.AddWithValue("r", a.RoleId);
            ur.Parameters.AddWithValue("l", a.LegalEntityId);
            await ur.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
