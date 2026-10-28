using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain.Enums;

namespace Proofly.Data.Evidence;

public static class EvidencePackJobs
{
    public const string Generate = "evidence_pack.generate";
}

public sealed record GenerateEvidencePackPayload(string PackId);

public sealed record PackRunInfo(string RunId, string LegalEntityId);

public sealed record RequestedPack(string PackId, string RunId, DateTimeOffset CreatedAt);

public enum RequestOutcome { Created, RunNotFound, AlreadyGenerating }

public sealed record RequestResult(RequestOutcome Outcome, RequestedPack? Pack);

public sealed record DownloadablePack(string PackId, string RunId, string LegalEntityId, string Sha256, long SizeBytes);

public interface IEvidencePackCommands
{
    /// <summary>Run id and legal entity, or null when the run does not exist (scope is enforced by the caller via the entity).</summary>
    Task<PackRunInfo?> GetRunAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// One transaction: insert the GENERATING row, enqueue the <c>evidence_pack.generate</c> job (run_id set) and write
    /// EVIDENCE_PACK_GENERATED. A run that already has a GENERATING pack is not queued twice.
    /// </summary>
    Task<RequestResult> RequestAsync(string runId, string actorUserId, string? requestId, CancellationToken ct = default);

    /// <summary>The newest READY pack of the run, or null.</summary>
    Task<DownloadablePack?> GetDownloadableAsync(string runId, CancellationToken ct = default);

    /// <summary>Writes EVIDENCE_PACK_DOWNLOADED (every attempt, delivered or refused) in its own transaction.</summary>
    Task RecordDownloadAsync(DownloadablePack pack, string actorUserId, string? requestId, string outcome, CancellationToken ct = default);

    /// <summary>Writes EVIDENCE_ACCESS_DENIED (one event per denied request) in its own transaction. <paramref name="operation"/> is the contract operation id.</summary>
    Task RecordAccessDeniedAsync(string runId, string actorUserId, string operation, string? requestId, CancellationToken ct = default);
}

public sealed class EvidencePackCommands(NpgsqlDataSource ds, IJobQueue jobs, IAuditWriter audit) : IEvidencePackCommands
{
    public async Task<PackRunInfo?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = ds.CreateCommand("SELECT legal_entity_id FROM runs WHERE id = @r");
        cmd.Parameters.AddWithValue("r", runId);
        return await cmd.ExecuteScalarAsync(ct) is string le ? new PackRunInfo(runId, le) : null;
    }

    private static async Task<AuditActor> ActorAsync(NpgsqlConnection c, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT name FROM users WHERE id = @u", c, tx);
        cmd.Parameters.AddWithValue("u", userId);
        return new AuditActor(ActorType.User, userId, await cmd.ExecuteScalarAsync(ct) as string ?? userId);
    }

    public async Task<RequestResult> RequestAsync(string runId, string actorUserId, string? requestId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialise requests per run: the run row lock makes the "already generating" check race-free.
        string? ruleset;
        await using (var lck = new NpgsqlCommand("SELECT ruleset_version FROM runs WHERE id = @r FOR UPDATE", conn, tx))
        {
            lck.Parameters.AddWithValue("r", runId);
            ruleset = await lck.ExecuteScalarAsync(ct) as string;
        }
        if (ruleset is null) { return new(RequestOutcome.RunNotFound, null); }

        await using (var chk = new NpgsqlCommand("SELECT 1 FROM evidence_packs WHERE run_id = @r AND status = 'GENERATING' LIMIT 1", conn, tx))
        {
            chk.Parameters.AddWithValue("r", runId);
            if (await chk.ExecuteScalarAsync(ct) is not null) { return new(RequestOutcome.AlreadyGenerating, null); }
        }

        var packId = "EP-" + Guid.NewGuid().ToString("N");
        DateTimeOffset createdAt;
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO evidence_packs (id, run_id, status, created_by_user_id) VALUES (@id, @r, 'GENERATING', @u) RETURNING created_at", conn, tx))
        {
            ins.Parameters.AddWithValue("id", packId);
            ins.Parameters.AddWithValue("r", runId);
            ins.Parameters.AddWithValue("u", actorUserId);
            createdAt = new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await ins.ExecuteScalarAsync(ct))!, DateTimeKind.Utc));
        }
        await jobs.EnqueueAsync(new JobRequest(EvidencePackJobs.Generate, new GenerateEvidencePackPayload(packId),
            DedupeKey: $"{EvidencePackJobs.Generate}:{packId}", RunId: runId), tx, ct);

        await audit.AppendAsync(tx, new AuditEventInput("EVIDENCE_PACK_GENERATED", await ActorAsync(conn, tx, actorUserId, ct), "EVIDENCE_PACK", packId,
            "Evidence pack generation requested", Reason: "Evidence pack requested", RunId: runId, RuleVersion: ruleset, RequestId: requestId,
            AfterJson: JsonSerializer.Serialize(new { status = "GENERATING" })), ct);
        await tx.CommitAsync(ct);
        return new(RequestOutcome.Created, new RequestedPack(packId, runId, createdAt));
    }

    public async Task<DownloadablePack?> GetDownloadableAsync(string runId, CancellationToken ct = default)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT p.id, r.legal_entity_id, p.sha256, p.size_bytes FROM evidence_packs p JOIN runs r ON r.id = p.run_id
            WHERE p.run_id = @r AND p.status = 'READY' ORDER BY p.created_at DESC, p.id DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("r", runId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? new DownloadablePack(rd.GetString(0), runId, rd.GetString(1), rd.GetString(2).Trim(), rd.GetInt64(3)) : null;
    }

    public async Task RecordDownloadAsync(DownloadablePack pack, string actorUserId, string? requestId, string outcome, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await audit.AppendAsync(tx, new AuditEventInput("EVIDENCE_PACK_DOWNLOADED", await ActorAsync(conn, tx, actorUserId, ct), "EVIDENCE_PACK", pack.PackId,
            outcome == "DELIVERED" ? "Evidence pack downloaded" : "Evidence pack download refused: integrity check failed",
            Reason: "Evidence pack export", RunId: pack.RunId, RequestId: requestId,
            AfterJson: JsonSerializer.Serialize(new { outcome, sha256 = pack.Sha256, sizeBytes = pack.SizeBytes })), ct);
        await tx.CommitAsync(ct);
    }

    public async Task RecordAccessDeniedAsync(string runId, string actorUserId, string operation, string? requestId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await audit.AppendAsync(tx, new AuditEventInput("EVIDENCE_ACCESS_DENIED", await ActorAsync(conn, tx, actorUserId, ct), "EVIDENCE_PACK", "run:" + runId,
            "Evidence pack access denied: the caller lacks permission or scope",
            Reason: "Evidence pack access denied", RunId: runId, RequestId: requestId,
            AfterJson: JsonSerializer.Serialize(new { operation, outcome = "DENIED" })), ct);
        await tx.CommitAsync(ct);
    }
}

/// <summary>
/// Handler of <c>evidence_pack.generate</c>. Idempotent (at-least-once jobs): a READY pack is left alone. Builds the pack,
/// stores it content-addressed (never overwritten) and marks the row READY with an audit event in the same transaction;
/// on error marks it FAILED with the reason and rethrows so the queue retries (a retry flips it back to GENERATING).
/// </summary>
public sealed class EvidencePackJobHandler(NpgsqlDataSource ds, EvidencePackBuilder builder, EvidenceStore store, IAuditWriter audit)
    : IJobHandler<GenerateEvidencePackPayload>
{
    public async Task HandleAsync(GenerateEvidencePackPayload payload, JobContext context, CancellationToken ct)
    {
        string runId; string? userId; DateTimeOffset createdAt; string status;
        await using (var cmd = ds.CreateCommand("SELECT run_id, created_by_user_id, created_at, status FROM evidence_packs WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", payload.PackId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { throw new PermanentJobException($"Evidence pack '{payload.PackId}' does not exist."); }
            runId = r.GetString(0); userId = r.IsDBNull(1) ? null : r.GetString(1); createdAt = r.GetFieldValue<DateTimeOffset>(2); status = r.GetString(3);
        }
        if (status == "READY") { return; }

        await SetStatus(payload.PackId, "GENERATING", null, ct);
        try
        {
            var build = await builder.BuildAsync(new EvidencePackRequest(runId, userId, createdAt), ct);
            using var content = new MemoryStream(build.Bytes, writable: false);
            var obj = await store.WriteAsync(content, ct);

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var upd = new NpgsqlCommand("""
                UPDATE evidence_packs SET status = 'READY', sha256 = @sha, size_bytes = @size, storage_ref = @ref,
                       contents = @contents, failure_reason = NULL WHERE id = @id
                """, conn, tx))
            {
                upd.Parameters.AddWithValue("id", payload.PackId);
                upd.Parameters.AddWithValue("sha", obj.Sha256);
                upd.Parameters.AddWithValue("size", obj.Length);
                upd.Parameters.AddWithValue("ref", $"{obj.Sha256[..2]}/{obj.Sha256}");
                upd.Parameters.Add(new NpgsqlParameter("contents", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(build.Files.Select(f => f.Path)) });
                await upd.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, new AuditEventInput("EVIDENCE_PACK_GENERATED", AuditActor.System("evidence-pack-worker"), "EVIDENCE_PACK", payload.PackId,
                "Evidence pack generated", Reason: "Evidence pack generation completed", RunId: runId,
                AfterJson: JsonSerializer.Serialize(new { status = "READY", sha256 = obj.Sha256, sizeBytes = obj.Length, resultHash = build.ResultHash })), ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (EvidencePackRunNotFoundException e)
        {
            await SetStatus(payload.PackId, "FAILED", e.Message, CancellationToken.None);
            throw new PermanentJobException(e.Message, e);
        }
        catch (Exception e)
        {
            var reason = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            await SetStatus(payload.PackId, "FAILED", reason, CancellationToken.None);
            throw;
        }
    }

    private async Task SetStatus(string id, string status, string? reason, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE evidence_packs SET status = @s, failure_reason = @f WHERE id = @id AND status <> 'READY'");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("f", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class EvidenceDataModule
{
    /// <summary>Command side, builder and the evidence store (<c>Evidence:Directory</c>). Requires AddData and AddAudit.</summary>
    public static IServiceCollection AddEvidenceData(this IServiceCollection s, IConfiguration configuration)
    {
        s.TryAddSingleton(new EvidenceOptions { Directory = configuration["Evidence:Directory"] ?? "" });
        s.TryAddSingleton<EvidenceStore>();
        if (!s.Any(d => d.ServiceType == typeof(IJobQueue))) { s.AddJobs(); }
        s.AddSingleton<EvidencePackBuilder>();
        s.AddSingleton<IEvidencePackCommands, EvidencePackCommands>();
        return s;
    }
}
