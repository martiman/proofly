using System.Text.Json;
using Npgsql;
using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Data.Ingestion;
using Microsoft.Extensions.DependencyInjection;

namespace Proofly.Data.Sources.Commands;

public enum SourceCommandStatus { Ok, NotFound, VersionMismatch, InvalidState, Invalid }

/// <summary>Outcome of a source command. <see cref="Detail"/> explains every non-Ok status; <see cref="Pointer"/> names the field for Invalid.</summary>
public sealed record SourceCommandResult(SourceCommandStatus Status, string? Detail = null, string? Pointer = null)
{
    public static SourceCommandResult Ok { get; } = new(SourceCommandStatus.Ok);
}

public interface ISourceCommandRepository
{
    /// <summary>Re-queues ingestion of a delivery (one transaction: state, audit DELIVERY_INGESTION_RETRIED, new delivery.ingest job).</summary>
    Task<SourceCommandResult> RetryIngestionAsync(string deliveryId, int expectedVersion, AuditActor actor, string reason, string? requestId, CancellationToken ct = default);

    /// <summary>Links a correcting delivery and marks the original SUPERSEDED (one transaction with audit DELIVERY_SUPERSEDED). The original is kept.</summary>
    Task<SourceCommandResult> MarkSupersededAsync(string deliveryId, string supersededByDeliveryId, int expectedVersion, AuditActor actor, string reason, string? requestId, CancellationToken ct = default);
}

public sealed class SourceCommandRepository(NpgsqlDataSource db, IAuditWriter audit, IJobQueue jobs) : ISourceCommandRepository
{
    private sealed record Row(string Id, string? RunId, string? LegalEntityId, string SourceName, DateOnly BusinessDate, string State, bool Ingested, int Version, string? Supersedes);

    private const string LockSql = """
        SELECT d.id, d.run_id, COALESCE(d.legal_entity_id, r.legal_entity_id), d.source_name, d.business_date, d.state,
               d.ingested_at IS NOT NULL, d.version, d.supersedes_delivery_id
        FROM source_deliveries d LEFT JOIN runs r ON r.id = d.run_id
        WHERE d.id = ANY(@ids) ORDER BY d.id FOR UPDATE OF d
        """;

    private static async Task<Dictionary<string, Row>> LockAsync(NpgsqlTransaction tx, string[] ids, CancellationToken ct)
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        await using var c = new NpgsqlCommand(LockSql, tx.Connection, tx);
        c.Parameters.AddWithValue("ids", ids);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i).Trim();
            rows[r.GetString(0)] = new Row(r.GetString(0), S(1), S(2), r.GetString(3), DateOnly.FromDateTime(r.GetDateTime(4)), r.GetString(5), r.GetBoolean(6), r.GetInt32(7), S(8));
        }
        return rows;
    }

    private static string Snapshot(Row r, string? state = null, bool? ingested = null, string? supersededBy = null) =>
        JsonSerializer.Serialize(new { state = state ?? r.State, ingested = ingested ?? r.Ingested, version = r.Version, supersededBy });

    public async Task<SourceCommandResult> RetryIngestionAsync(string deliveryId, int expectedVersion, AuditActor actor, string reason, string? requestId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = await LockAsync(tx, [deliveryId], ct);
        if (!rows.TryGetValue(deliveryId, out var d)) { return new(SourceCommandStatus.NotFound); }
        if (d.Version != expectedVersion) { return new(SourceCommandStatus.VersionMismatch); }

        bool duplicate;
        await using (var q = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM delivery_rejections WHERE delivery_id = @d AND row_number IS NULL AND reason_code = @c AND NOT superseded_by_retry)", conn, tx))
        {
            q.Parameters.AddWithValue("d", deliveryId);
            q.Parameters.AddWithValue("c", DeliveryRules.DuplicateReasonCode);
            duplicate = (bool)(await q.ExecuteScalarAsync(ct))!;
        }
        if (!DeliveryRules.CanRetryIngestion(d.State, d.Ingested, duplicate))
        {
            return new(SourceCommandStatus.InvalidState, duplicate
                ? "A duplicate delivery cannot be retried; the original delivery stands."
                : $"Ingestion can only be retried for a rejected delivery or a received delivery that was not ingested (state {d.State}).");
        }

        // The ingestor only processes RECEIVED deliveries that were never ingested, so a rejected delivery is re-opened for it.
        // Earlier rejection reasons stay in delivery_rejections as history (flagged superseded_by_retry so checks and listings show the
        // latest attempt only); nothing is deleted.
        await using (var sup = new NpgsqlCommand("UPDATE delivery_rejections SET superseded_by_retry = true WHERE delivery_id = @d AND NOT superseded_by_retry", conn, tx))
        {
            sup.Parameters.AddWithValue("d", deliveryId);
            await sup.ExecuteNonQueryAsync(ct);
        }
        int newVersion;
        try
        {
            await using var up = new NpgsqlCommand("""
                UPDATE source_deliveries SET state = 'RECEIVED', ingested_at = NULL, accepted_record_count = 0, rejected_record_count = 0, total_row_count = NULL
                WHERE id = @id AND version = @v RETURNING version
                """, conn, tx);
            up.Parameters.AddWithValue("id", deliveryId);
            up.Parameters.AddWithValue("v", expectedVersion);
            if (await up.ExecuteScalarAsync(ct) is not int nv) { return new(SourceCommandStatus.VersionMismatch); }
            newVersion = nv;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new(SourceCommandStatus.InvalidState, "Another delivery of this source and date with the same content is already accepted.");
        }

        await audit.AppendAsync(tx, new AuditEventInput("DELIVERY_INGESTION_RETRIED", actor, "DELIVERY", deliveryId,
            $"Ingestion of delivery {deliveryId} ({d.SourceName}, {d.BusinessDate:yyyy-MM-dd}) retried",
            Reason: reason, RunId: d.RunId, RequestId: requestId,
            BeforeJson: Snapshot(d), AfterJson: Snapshot(d, state: "RECEIVED", ingested: false)), ct);

        // A new dedupe key per retry: the version after this update is unique to this retry, so the job is never swallowed by an earlier one.
        await jobs.EnqueueAsync(new JobRequest(IngestionJobs.IngestDelivery, new IngestDeliveryPayload(deliveryId),
            DedupeKey: $"{IngestionJobs.IngestDelivery}:{deliveryId}:retry:{newVersion}", RunId: d.RunId, DeliveryId: deliveryId), tx, ct);
        await tx.CommitAsync(ct);
        return SourceCommandResult.Ok;
    }

    public async Task<SourceCommandResult> MarkSupersededAsync(string deliveryId, string supersededByDeliveryId, int expectedVersion, AuditActor actor, string reason, string? requestId, CancellationToken ct = default)
    {
        const string pointer = "/supersededByDeliveryId";
        if (deliveryId == supersededByDeliveryId) { return new(SourceCommandStatus.Invalid, "A delivery cannot supersede itself.", pointer); }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = await LockAsync(tx, [deliveryId, supersededByDeliveryId], ct);
        if (!rows.TryGetValue(deliveryId, out var d)) { return new(SourceCommandStatus.NotFound); }
        if (d.Version != expectedVersion) { return new(SourceCommandStatus.VersionMismatch); }

        bool already;
        await using (var q = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM source_deliveries WHERE supersedes_delivery_id = @d AND state <> 'REJECTED')", conn, tx))
        {
            q.Parameters.AddWithValue("d", deliveryId);
            already = (bool)(await q.ExecuteScalarAsync(ct))!;
        }
        if (!DeliveryRules.CanSupersede(d.State, already))
        {
            return new(SourceCommandStatus.InvalidState, already ? "The delivery already has a correcting delivery."
                : $"Only a received or qualified delivery can be superseded (state {d.State}).");
        }

        if (!rows.TryGetValue(supersededByDeliveryId, out var c) || c.LegalEntityId != d.LegalEntityId || c.SourceName != d.SourceName || c.BusinessDate != d.BusinessDate)
        {
            return new(SourceCommandStatus.Invalid, "The correcting delivery must exist and belong to the same source, business date and legal entity.", pointer);
        }
        if (c.State is "REJECTED" or "SUPERSEDED") { return new(SourceCommandStatus.InvalidState, $"A {c.State.ToLowerInvariant()} delivery cannot be the correcting delivery."); }
        if (c.Supersedes is not null && c.Supersedes != deliveryId) { return new(SourceCommandStatus.InvalidState, $"The correcting delivery already corrects {c.Supersedes}."); }

        await using (var up = new NpgsqlCommand("UPDATE source_deliveries SET supersedes_delivery_id = @o WHERE id = @c", conn, tx))
        {
            up.Parameters.AddWithValue("o", deliveryId);
            up.Parameters.AddWithValue("c", supersededByDeliveryId);
            await up.ExecuteNonQueryAsync(ct);
        }
        await using (var up = new NpgsqlCommand("UPDATE source_deliveries SET state = 'SUPERSEDED' WHERE id = @id AND version = @v", conn, tx))
        {
            up.Parameters.AddWithValue("id", deliveryId);
            up.Parameters.AddWithValue("v", expectedVersion);
            if (await up.ExecuteNonQueryAsync(ct) != 1) { return new(SourceCommandStatus.VersionMismatch); }
        }
        await audit.AppendAsync(tx, new AuditEventInput("DELIVERY_SUPERSEDED", actor, "DELIVERY", deliveryId,
            $"Delivery {deliveryId} ({d.SourceName}, {d.BusinessDate:yyyy-MM-dd}) superseded by {supersededByDeliveryId}",
            Reason: reason, RunId: d.RunId, RequestId: requestId,
            BeforeJson: Snapshot(d), AfterJson: Snapshot(d, state: "SUPERSEDED", supersededBy: supersededByDeliveryId)), ct);
        await tx.CommitAsync(ct);
        return SourceCommandResult.Ok;
    }
}

public static class SourceCommandsDataModule
{
    public static IServiceCollection AddSourceCommands(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IJobQueue))) { services.AddJobs(); }
        services.AddSingleton<ISourceCommandRepository>(sp => new SourceCommandRepository(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditWriter>(), sp.GetRequiredService<IJobQueue>()));
        return services;
    }
}
