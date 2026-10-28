using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Domain.Enums;

namespace Proofly.Data.Safeguarding.Commands;

public enum CommandOutcome { Ok, NotFound, InvalidState, PreconditionFailed, SeparationOfDuties, Validation, RunLocked, StaleProposal, RunSuperseded }

/// <summary>Outcome of an adjustment command. <see cref="AdjustmentId"/> is set on success; Pointer/Detail describe a failure.</summary>
public sealed record AdjustmentCommandResult(CommandOutcome Outcome, string? AdjustmentId = null, string? RunId = null, string? Pointer = null, string? Detail = null);

public sealed record ProposeAdjustmentCommand(
    string RunId, string UserId, string Currency, decimal Amount, string Reason, IReadOnlyList<string> EvidenceRefs, DateOnly? ExpiresOn, string? RequestId);

public sealed record DecideAdjustmentCommand(
    string AdjustmentId, string UserId, bool Approve, string Reason, int ExpectedVersion, int? ProposalVersion, string? RequestId);

/// <summary>Minimal facts for authorisation and ETag checks before a command runs.</summary>
public sealed record AdjustmentHeader(string Id, string RunId, string LegalEntityId, int Version, string Status);

public sealed record ExpireAdjustmentsResult(int Expired);

public interface IAdjustmentCommands
{
    /// <summary>Run header (id, entity) when in scope; null otherwise.</summary>
    Task<string?> FindRunEntityAsync(string runId, EntityScope scope, CancellationToken ct);
    Task<AdjustmentHeader?> FindAdjustmentAsync(string adjustmentId, EntityScope scope, CancellationToken ct);
    Task<AdjustmentCommandResult> ProposeAsync(ProposeAdjustmentCommand cmd, CancellationToken ct);
    Task<AdjustmentCommandResult> DecideAsync(DecideAdjustmentCommand cmd, CancellationToken ct);
    /// <summary>Marks APPROVED adjustments with expires_on before <paramref name="asOf"/> EXPIRED (SYSTEM actor, audited). Idempotent.</summary>
    Task<ExpireAdjustmentsResult> ExpireAsync(DateOnly asOf, CancellationToken ct);
}

/// <summary>
/// One transaction per command: guarded UPDATE (version), state checks, business change, audit event, commit.
/// Control results and signed runs are never touched: runs that are SIGNED_OFF or SUPERSEDED reject new adjustments and
/// their adjustments are not expired. An approved or expired adjustment flags the stored position of its currency <c>stale</c> until the run is reprocessed.
/// </summary>
public sealed class AdjustmentCommands(NpgsqlDataSource db, IAuditWriter audit) : IAdjustmentCommands
{
    public const string ExpireJobType = "safeguarding.adjustment.expire";

    public async Task<string?> FindRunEntityAsync(string runId, EntityScope scope, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT legal_entity_id FROM runs WHERE id=@id AND (@all OR legal_entity_id = ANY(@ids))");
        c.Parameters.AddWithValue("id", runId);
        Scope(c, scope);
        return await c.ExecuteScalarAsync(ct) as string;
    }

    public async Task<AdjustmentHeader?> FindAdjustmentAsync(string adjustmentId, EntityScope scope, CancellationToken ct)
    {
        await using var c = db.CreateCommand(
            "SELECT a.id, a.run_id, r.legal_entity_id, a.version, a.status FROM safeguarding_adjustments a JOIN runs r ON r.id=a.run_id WHERE a.id=@id AND (@all OR r.legal_entity_id = ANY(@ids))");
        c.Parameters.AddWithValue("id", adjustmentId);
        Scope(c, scope);
        await using var rd = await c.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new AdjustmentHeader(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetString(4)) : null;
    }

    private static void Scope(NpgsqlCommand c, EntityScope scope)
    {
        c.Parameters.AddWithValue("all", scope.All);
        c.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = scope.Ids.ToArray() });
    }

    private static string D(decimal v) => v.ToString(CultureInfo.InvariantCulture);

    public async Task<AdjustmentCommandResult> ProposeAsync(ProposeAdjustmentCommand cmd, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string entity, runState, signOff; DateOnly businessDate; string[] currencies;
        await using (var c = new NpgsqlCommand(
            "SELECT r.legal_entity_id, r.state, r.sign_off_state, r.business_date, le.currencies FROM runs r JOIN legal_entities le ON le.id=r.legal_entity_id WHERE r.id=@id FOR UPDATE OF r", conn, tx))
        {
            c.Parameters.AddWithValue("id", cmd.RunId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) { return new(CommandOutcome.NotFound); }
            entity = rd.GetString(0);
            runState = rd.GetString(1); signOff = rd.GetString(2);
            businessDate = DateOnly.FromDateTime(rd.GetDateTime(3));
            currencies = rd.GetFieldValue<string[]>(4);
        }
        if (runState == "SUPERSEDED") { return new(CommandOutcome.RunSuperseded, RunId: cmd.RunId, Detail: "The run is superseded by a newer version; it cannot take new adjustments."); }
        if (runState is "SIGNED_OFF" || signOff == "SIGNED_OFF")
        {
            return new(CommandOutcome.RunLocked, RunId: cmd.RunId, Detail: $"The run is {runState}/{signOff} and cannot take new adjustments.");
        }
        if (!currencies.Contains(cmd.Currency)) { return new(CommandOutcome.Validation, Pointer: "/currency", Detail: $"Currency {cmd.Currency} does not belong to the run's legal entity."); }
        if (cmd.ExpiresOn is { } e && e < businessDate) { return new(CommandOutcome.Validation, Pointer: "/expiresOn", Detail: "expiresOn must not be before the run's business date."); }

        var id = "ADJ-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var refsJson = JsonSerializer.Serialize(cmd.EvidenceRefs);
        await using (var c = new NpgsqlCommand(
            "INSERT INTO safeguarding_adjustments (id, run_id, currency, amount, reason, status, proposed_by_user_id, evidence_refs, expires_on) VALUES (@id,@run,@ccy,@amt,@reason,'PROPOSED',@u,@refs,@exp)", conn, tx))
        {
            c.Parameters.AddWithValue("id", id);
            c.Parameters.AddWithValue("run", cmd.RunId);
            c.Parameters.AddWithValue("ccy", cmd.Currency);
            c.Parameters.AddWithValue("amt", NpgsqlDbType.Numeric, cmd.Amount);
            c.Parameters.AddWithValue("reason", cmd.Reason);
            c.Parameters.AddWithValue("u", cmd.UserId);
            c.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Jsonb) { Value = refsJson });
            c.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.Date) { Value = cmd.ExpiresOn is { } d ? d : DBNull.Value });
            await c.ExecuteNonQueryAsync(ct);
        }

        string policy;
        await using (var c = new NpgsqlCommand("SELECT config_version FROM config_rules WHERE kind='approval_policy' AND enabled ORDER BY updated_at DESC, id LIMIT 1", conn, tx))
        {
            policy = await c.ExecuteScalarAsync(ct) as string ?? "adr-0005";
        }
        await using (var c = new NpgsqlCommand(
            @"INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, evidence_refs, policy_version)
              VALUES (@id,'SAFEGUARDING_ADJUSTMENT',@obj,@run,1,@u,'APPROVER','PENDING',@refs,@pol)", conn, tx))
        {
            c.Parameters.AddWithValue("id", "AP-" + id);
            c.Parameters.AddWithValue("obj", id);
            c.Parameters.AddWithValue("run", cmd.RunId);
            c.Parameters.AddWithValue("u", cmd.UserId);
            c.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Jsonb) { Value = refsJson });
            c.Parameters.AddWithValue("pol", policy);
            await c.ExecuteNonQueryAsync(ct);
        }

        await audit.AppendAsync(tx, new AuditEventInput("ADJUSTMENT_PROPOSED", new AuditActor(ActorType.User, cmd.UserId, cmd.UserId), "ADJUSTMENT", id,
            $"Proposed {cmd.Currency} adjustment {D(cmd.Amount)}", cmd.Reason, cmd.RunId, null, cmd.RequestId, null,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["status"] = "PROPOSED", ["currency"] = cmd.Currency, ["amount"] = D(cmd.Amount), ["expiresOn"] = cmd.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["evidenceRefs"] = cmd.EvidenceRefs }),
            entity), ct);
        await tx.CommitAsync(ct);
        return new(CommandOutcome.Ok, id, cmd.RunId);
    }

    public async Task<AdjustmentCommandResult> DecideAsync(DecideAdjustmentCommand cmd, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string runId, entity, status, proposer, runState, signOff, adjCurrency; int version;
        await using (var c = new NpgsqlCommand(
            @"SELECT a.run_id, r.legal_entity_id, a.status, a.proposed_by_user_id, a.version, r.state, r.sign_off_state, a.currency::text
              FROM safeguarding_adjustments a JOIN runs r ON r.id=a.run_id WHERE a.id=@id FOR UPDATE OF a", conn, tx))
        {
            c.Parameters.AddWithValue("id", cmd.AdjustmentId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) { return new(CommandOutcome.NotFound); }
            runId = rd.GetString(0); entity = rd.GetString(1); status = rd.GetString(2); proposer = rd.GetString(3); version = rd.GetInt32(4);
            runState = rd.GetString(5); signOff = rd.GetString(6); adjCurrency = rd.GetString(7);
        }
        if (version != cmd.ExpectedVersion) { return new(CommandOutcome.PreconditionFailed); }
        if (status != "PROPOSED") { return new(CommandOutcome.InvalidState, Detail: $"Only a PROPOSED adjustment can be decided; this one is {status}."); }
        if (proposer == cmd.UserId) { return new(CommandOutcome.SeparationOfDuties, Detail: "The proposer cannot decide their own adjustment."); }
        if (runState == "SUPERSEDED") { return new(CommandOutcome.RunSuperseded, Detail: "The run is superseded by a newer version; its adjustments are frozen."); }
        if (runState is "SIGNED_OFF" || signOff == "SIGNED_OFF") { return new(CommandOutcome.RunLocked, Detail: $"The run is {runState}/{signOff}; its adjustments are frozen."); }

        string approvalId; int proposalVersion; int approvalRowVersion;
        await using (var c = new NpgsqlCommand(
            "SELECT id, proposal_version, version FROM approvals WHERE object_type='SAFEGUARDING_ADJUSTMENT' AND object_id=@id AND status='PENDING' ORDER BY proposed_at DESC, id DESC LIMIT 1 FOR UPDATE", conn, tx))
        {
            c.Parameters.AddWithValue("id", cmd.AdjustmentId);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) { return new(CommandOutcome.InvalidState, Detail: "The adjustment has no pending approval request."); }
            approvalId = rd.GetString(0); proposalVersion = rd.GetInt32(1); approvalRowVersion = rd.GetInt32(2);
        }
        if (cmd.ProposalVersion is { } pv && pv != proposalVersion) { return new(CommandOutcome.StaleProposal, Detail: $"The proposal changed (reviewed version {pv}, current {proposalVersion}); re-read it."); }

        var newStatus = cmd.Approve ? "APPROVED" : "REJECTED";
        await using (var c = new NpgsqlCommand(
            @"UPDATE approvals SET status=@s, decision=@d, approver_user_id=@u, rationale=@r, decided_at=now() WHERE id=@id AND version=@v", conn, tx))
        {
            c.Parameters.AddWithValue("s", newStatus);
            c.Parameters.AddWithValue("d", cmd.Approve ? "APPROVE" : "REJECT");
            c.Parameters.AddWithValue("u", cmd.UserId);
            c.Parameters.AddWithValue("r", cmd.Reason);
            c.Parameters.AddWithValue("id", approvalId);
            c.Parameters.AddWithValue("v", approvalRowVersion);
            if (await c.ExecuteNonQueryAsync(ct) != 1) { return new(CommandOutcome.PreconditionFailed); }
        }
        await using (var c = new NpgsqlCommand("UPDATE safeguarding_adjustments SET status=@s WHERE id=@id AND version=@v AND status='PROPOSED'", conn, tx))
        {
            c.Parameters.AddWithValue("s", newStatus);
            c.Parameters.AddWithValue("id", cmd.AdjustmentId);
            c.Parameters.AddWithValue("v", cmd.ExpectedVersion);
            if (await c.ExecuteNonQueryAsync(ct) != 1) { return new(CommandOutcome.PreconditionFailed); }
        }

        if (cmd.Approve) { await MarkStaleAsync(conn, tx, runId, adjCurrency, ct); }
        await audit.AppendAsync(tx, new AuditEventInput("ADJUSTMENT_DECIDED", new AuditActor(ActorType.User, cmd.UserId, cmd.UserId), "ADJUSTMENT", cmd.AdjustmentId,
            $"{(cmd.Approve ? "Approved" : "Rejected")} safeguarding adjustment; the run's safeguarding position is stale until a manual reprocess", cmd.Reason, runId, null, cmd.RequestId,
            JsonSerializer.Serialize(new { status }), JsonSerializer.Serialize(new { status = newStatus, approvalId }), entity), ct);
        await tx.CommitAsync(ct);
        return new(CommandOutcome.Ok, cmd.AdjustmentId, runId);
    }

    /// <summary>The stored position of the run no longer includes this adjustment; flag it until the run is reprocessed.</summary>
    private static async Task MarkStaleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string runId, string currency, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("UPDATE safeguarding_positions SET stale = true, updated_at = now() WHERE run_id = @r AND currency = @c", conn, tx);
        c.Parameters.AddWithValue("r", runId);
        c.Parameters.AddWithValue("c", currency);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<ExpireAdjustmentsResult> ExpireAsync(DateOnly asOf, CancellationToken ct)
    {
        var due = new List<string>();
        await using (var c = db.CreateCommand(
            @"SELECT a.id FROM safeguarding_adjustments a JOIN runs r ON r.id=a.run_id
              WHERE a.status='APPROVED' AND a.expires_on < @d AND r.state NOT IN ('SIGNED_OFF','SUPERSEDED') AND r.sign_off_state <> 'SIGNED_OFF' ORDER BY a.id"))
        {
            c.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = asOf });
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) { due.Add(rd.GetString(0)); }
        }

        var expired = 0;
        foreach (var id in due)
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string runId, entity, currency; DateOnly exp;
            await using (var c = new NpgsqlCommand(
                "SELECT a.run_id, r.legal_entity_id, a.expires_on, a.currency::text FROM safeguarding_adjustments a JOIN runs r ON r.id=a.run_id WHERE a.id=@id AND a.status='APPROVED' AND a.expires_on < @d FOR UPDATE OF a SKIP LOCKED", conn, tx))
            {
                c.Parameters.AddWithValue("id", id);
                c.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = asOf });
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) { continue; } // already expired or being handled elsewhere
                runId = rd.GetString(0); entity = rd.GetString(1); exp = DateOnly.FromDateTime(rd.GetDateTime(2)); currency = rd.GetString(3);
            }
            await using (var c = new NpgsqlCommand("UPDATE safeguarding_adjustments SET status='EXPIRED' WHERE id=@id AND status='APPROVED'", conn, tx))
            {
                c.Parameters.AddWithValue("id", id);
                await c.ExecuteNonQueryAsync(ct);
            }
            await MarkStaleAsync(conn, tx, runId, currency, ct);
            await audit.AppendAsync(tx, new AuditEventInput("ADJUSTMENT_EXPIRED", AuditActor.System("safeguarding.adjustment.expire"), "ADJUSTMENT", id,
                "Approved adjustment expired; the run's safeguarding position is stale until a manual reprocess",
                $"Expired: expiresOn {exp:yyyy-MM-dd} is before business date {asOf:yyyy-MM-dd}", runId, null, null,
                JsonSerializer.Serialize(new { status = "APPROVED" }), JsonSerializer.Serialize(new { status = "EXPIRED" }), entity), ct);
            await tx.CommitAsync(ct);
            expired++;
        }
        return new(expired);
    }
}

public sealed record ExpireAdjustmentsPayload(string AsOf);

/// <summary>Job handler for <c>safeguarding.adjustment.expire</c>; idempotent (an expired adjustment is no longer selected).</summary>
public sealed class ExpireAdjustmentsHandler(IAdjustmentCommands commands) : IJobHandler<ExpireAdjustmentsPayload>
{
    public async Task HandleAsync(ExpireAdjustmentsPayload payload, JobContext context, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(payload.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
        {
            throw new PermanentJobException($"Invalid asOf date '{payload.AsOf}'.");
        }
        await commands.ExpireAsync(asOf, ct);
    }
}

public static class SafeguardingCommandsDataModule
{
    public static IServiceCollection AddSafeguardingCommandsData(this IServiceCollection services) =>
        services.AddSingleton<IAdjustmentCommands, AdjustmentCommands>();
}
