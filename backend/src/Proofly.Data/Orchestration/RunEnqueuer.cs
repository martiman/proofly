using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;

namespace Proofly.Data.Orchestration;

/// <summary>
/// Enqueues <c>run.process</c> after a delivery was ingested successfully (RECEIVED or QUALIFIED) and every mandatory
/// source has an ingested, non-superseded delivery for the entity and business date. The dedupe key covers the set of
/// delivery ids and fingerprints, so a retry of the same ingestion creates no second job, while a different input set
/// (for example a correction) is a different job. The handler decides what to do with an existing run: only a run
/// without a result is processed; a processed run is never changed automatically (manual reprocess only, ADR 0005).
/// </summary>
public sealed class RunEnqueuer(NpgsqlDataSource dataSource, IJobQueue jobs, IngestionOptions ingestion)
{
    /// <returns>True when a new job was enqueued.</returns>
    public async Task<bool> OnDeliveryIngestedAsync(string deliveryId, CancellationToken ct = default)
    {
        string? le = null; DateOnly date = default; string? state = null;
        await using (var c = dataSource.CreateCommand("SELECT legal_entity_id, business_date, state FROM source_deliveries WHERE id=@id AND ingested_at IS NOT NULL"))
        {
            c.Parameters.AddWithValue("id", deliveryId);
            await using var r = await c.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { le = r.IsDBNull(0) ? null : r.GetString(0); date = r.GetFieldValue<DateOnly>(1); state = r.GetString(2); }
        }
        if (le is null || state is not ("RECEIVED" or "QUALIFIED")) return false;

        var current = new List<(string Id, string Source, string Fingerprint)>();
        await using (var c = dataSource.CreateCommand("""
            SELECT id, source_name, fingerprint_sha256 FROM source_deliveries
            WHERE legal_entity_id=@le AND business_date=@d AND state IN ('RECEIVED','QUALIFIED') AND ingested_at IS NOT NULL ORDER BY id
            """))
        {
            c.Parameters.AddWithValue("le", le);
            c.Parameters.AddWithValue("d", date);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) current.Add((r.GetString(0), r.GetString(1), r.GetString(2).Trim()));
        }
        var present = current.Select(x => x.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!ingestion.MandatorySources.All(present.Contains)) return false;

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", current.Select(x => $"{x.Id}:{x.Fingerprint}")))))[..16].ToLowerInvariant();
        var result = await jobs.EnqueueAsync(new JobRequest(RunJobs.ProcessRun, new ProcessRunPayload(le, date, null, "evidence"),
            DedupeKey: $"{RunJobs.ProcessRun}:{le}:{date:yyyyMMdd}:{key}", DeliveryId: deliveryId), null, ct);
        return result.Created;
    }
}
