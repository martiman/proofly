using System.Globalization;
using System.Text.Json;
using Npgsql;
using Proofly.Connectors.Abstractions;
using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Ingestion;

/// <summary>
/// Registers a delivery (manifest entry + stored evidence) in one transaction with its audit event and, when the
/// registration checks pass, the <c>delivery.ingest</c> job. Registration checks: schema version supported,
/// fingerprint not a duplicate, correction link present and valid for a second delivery of the same source/date,
/// sequence number contiguous; every check, and the advisory lock, is scoped to the legal entity as well as source/date. A failed check does not discard the delivery: it is stored as REJECTED with the
/// reasons in <c>delivery_rejections</c> so the attempt stays visible. Registration is serialised per source/date by
/// an advisory lock. Replaying the same delivery id and fingerprint returns the existing registration.
/// </summary>
public sealed class DeliveryRegistry(
    Npgsql.NpgsqlDataSource dataSource, IAuditWriter audit, IJobQueue jobs, ISourceAdapterRegistry adapters, IngestionOptions options)
{
    internal static readonly AuditActor System = AuditActor.System("ingestion");

    public async Task<RegistrationResult> RegisterAsync(DeliveryRegistration r, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        var m = r.Manifest;
        if (!EvidenceStore.IsValidSha256(r.FingerprintSha256)) throw new ArgumentException("Fingerprint must be a lower-case hex SHA-256.", nameof(r));
        if (string.IsNullOrWhiteSpace(m.DeliveryId) || string.IsNullOrWhiteSpace(m.SourceName)) throw new ArgumentException("Manifest needs source_name and delivery_id.", nameof(r));
        if (m.LegalEntityId is { } named && named != r.LegalEntityId)
            throw new ArgumentException($"Manifest names legal entity '{named}' but the delivery is registered for '{r.LegalEntityId}'.", nameof(r));
        var actor = r.Actor ?? System;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await IngestionSql.LockSourceDateAsync(tx, r.LegalEntityId, m.SourceName, m.BusinessDate, ct);

        await using (var q = new NpgsqlCommand("SELECT fingerprint_sha256, state FROM source_deliveries WHERE id = @id", conn, tx))
        {
            q.Parameters.AddWithValue("id", m.DeliveryId);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var same = rd.GetString(0).Trim() == r.FingerprintSha256;
                var existingState = DeliveryStates.Parse(rd.GetString(1));
                await rd.CloseAsync();
                if (same) return new RegistrationResult(m.DeliveryId, existingState, false, false, []);
                await audit.AppendAsync(tx, new AuditEventInput("DELIVERY_RECEIVED", actor, "DELIVERY", m.DeliveryId,
                    $"Delivery {m.DeliveryId} refused: id already registered with different content",
                    Reason: "Delivery id reused with a different fingerprint", RunId: r.RunId, RequestId: r.RequestId,
                    AfterJson: JsonSerializer.Serialize(new { refused = true, fingerprint = r.FingerprintSha256 })), ct);
                await tx.CommitAsync(ct);
                return new RegistrationResult(m.DeliveryId, null, false, true,
                    [new DeliveryFailure("DELIVERY_ID_CONFLICT", "Delivery id already registered with a different fingerprint.")]);
            }
        }

        var failures = new List<DeliveryFailure>();
        var adapter = adapters.Find(m.SourceName, r.Format);
        if (adapter is null)
            failures.Add(new(ReasonCodes.SchemaUnsupported, $"No adapter for source '{m.SourceName}' and format {r.Format}."));
        else if (!adapter.SupportedSchemaVersions.Contains(m.SchemaVersion))
            failures.Add(new(ReasonCodes.SchemaUnsupported, $"Schema version '{m.SchemaVersion}' is not supported for '{m.SourceName}'."));

        string? supersedes = null;
        var duplicateOf = await IngestionSql.ScalarAsync<string>(tx,
            "SELECT id FROM source_deliveries WHERE legal_entity_id=@le AND source_name=@s AND business_date=@d AND fingerprint_sha256=@f AND state <> 'REJECTED' LIMIT 1",
            ct, ("le", r.LegalEntityId), ("s", m.SourceName), ("d", m.BusinessDate), ("f", r.FingerprintSha256));
        if (duplicateOf is not null)
        {
            failures.Add(new(ReasonCodes.DuplicateDelivery, $"Same content already delivered as {duplicateOf}."));
        }
        else
        {
            var current = await IngestionSql.ScalarAsync<string>(tx,
                "SELECT id FROM source_deliveries WHERE legal_entity_id=@le AND source_name=@s AND business_date=@d AND state NOT IN ('REJECTED','SUPERSEDED') ORDER BY created_at DESC LIMIT 1",
                ct, ("le", r.LegalEntityId), ("s", m.SourceName), ("d", m.BusinessDate));
            if (m.CorrectionOfDeliveryId is null)
            {
                if (current is not null)
                    failures.Add(new(ReasonCodes.CorrectionLinkMissing, $"{current} already covers this source and date; a replacement must name it in correction_of_delivery_id."));
            }
            else if (m.CorrectionOfDeliveryId != current)
            {
                failures.Add(new(ReasonCodes.CorrectionLinkMissing, $"correction_of_delivery_id {m.CorrectionOfDeliveryId} is not the current delivery for this source and date."));
            }
            else
            {
                supersedes = current;
            }

            if (m.SequenceNumber is { } seq && m.CorrectionOfDeliveryId is null)
            {
                var last = await IngestionSql.ScalarAsync<int?>(tx,
                    "SELECT max(sequence_number) FROM source_deliveries WHERE legal_entity_id=@le AND source_name=@s AND state <> 'REJECTED'", ct, ("le", r.LegalEntityId), ("s", m.SourceName));
                if (last is { } l && seq != l + 1)
                    failures.Add(new(ReasonCodes.SequenceGap, $"Sequence {seq} does not follow the last accepted sequence {l}."));
            }
        }

        var state = failures.Count > 0 ? DeliveryState.Rejected : DeliveryState.Received;
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256,
              declared_record_count, expected_by, received_at, state, supersedes_delivery_id,
              source_format, sequence_number, rejected_record_count)
            VALUES (@id, @run, @le, @src, @date, @sv, @fn, @fp, @cnt, @exp, @rec, @state, @sup, @fmt, @seq, 0)
            """, conn, tx))
        {
            var p = ins.Parameters;
            p.AddWithValue("id", m.DeliveryId);
            p.AddWithValue("run", (object?)r.RunId ?? DBNull.Value);
            p.AddWithValue("le", r.LegalEntityId);
            p.AddWithValue("src", m.SourceName);
            p.AddWithValue("date", m.BusinessDate);
            p.AddWithValue("sv", m.SchemaVersion);
            p.AddWithValue("fn", m.FileName);
            p.AddWithValue("fp", r.FingerprintSha256);
            p.AddWithValue("cnt", (object?)m.DeclaredRecordCount ?? DBNull.Value);
            p.AddWithValue("exp", (object?)m.ExpectedBy?.ToUniversalTime() ?? DBNull.Value);
            p.AddWithValue("rec", (object?)m.ReceivedAt?.ToUniversalTime() ?? DBNull.Value);
            p.AddWithValue("state", DeliveryStates.Wire(state));
            p.AddWithValue("sup", (object?)supersedes ?? DBNull.Value);
            p.AddWithValue("fmt", WireNames.ToUpperSnake(r.Format));
            p.AddWithValue("seq", (object?)m.SequenceNumber ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in m.DeclaredTotals)
        {
            await using var ct1 = new NpgsqlCommand(
                "INSERT INTO delivery_control_totals (delivery_id, currency, declared_amount) VALUES (@d, @c, @a)", conn, tx);
            ct1.Parameters.AddWithValue("d", m.DeliveryId);
            ct1.Parameters.AddWithValue("c", t.Currency);
            ct1.Parameters.AddWithValue("a", t.Amount);
            await ct1.ExecuteNonQueryAsync(ct);
        }
        foreach (var f in failures) await IngestionSql.InsertFailureAsync(tx, m.DeliveryId, f, ct);

        var summary = state == DeliveryState.Rejected
            ? $"Delivery {m.DeliveryId} ({m.SourceName}, {m.BusinessDate:yyyy-MM-dd}) rejected at registration"
            : $"Delivery {m.DeliveryId} ({m.SourceName}, {m.BusinessDate:yyyy-MM-dd}) registered";
        await audit.AppendAsync(tx, new AuditEventInput("DELIVERY_RECEIVED", actor, "DELIVERY", m.DeliveryId, summary,
            Reason: failures.Count > 0 ? string.Join("; ", failures.Select(f => f.ReasonCode + ": " + f.Message)) : "Registration checks passed",
            RunId: r.RunId, RequestId: r.RequestId,
            AfterJson: JsonSerializer.Serialize(new
            {
                state = DeliveryStates.Wire(state), source = m.SourceName, businessDate = m.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                fingerprint = r.FingerprintSha256, supersedes, failures = failures.Select(f => f.ReasonCode),
            })), ct);

        if (state == DeliveryState.Received)
        {
            await jobs.EnqueueAsync(new JobRequest(IngestionJobs.IngestDelivery, new IngestDeliveryPayload(m.DeliveryId),
                DedupeKey: $"{IngestionJobs.IngestDelivery}:{m.DeliveryId}", RunId: r.RunId, DeliveryId: m.DeliveryId), tx, ct);
        }
        await tx.CommitAsync(ct);
        return new RegistrationResult(m.DeliveryId, state, true, false, failures);
    }

    /// <summary>
    /// Mandatory sources with no ingested, non-rejected, non-superseded delivery for the entity and business date
    /// (reason SOURCE_MISSING). A pure query: nothing is stored, so the check can be re-evaluated at any time.
    /// </summary>
    public async Task<IReadOnlyList<MissingSource>> FindMissingMandatorySourcesAsync(
        string legalEntityId, DateOnly businessDate, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT DISTINCT source_name FROM source_deliveries
            WHERE legal_entity_id=@le AND business_date=@d AND state IN ('RECEIVED','QUALIFIED') AND ingested_at IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("le", legalEntityId);
        cmd.Parameters.AddWithValue("d", businessDate);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var rd = await cmd.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) present.Add(rd.GetString(0));
        return options.MandatorySources.Where(s => !present.Contains(s))
            .Select(s => new MissingSource(s, ReasonCodes.SourceMissing, $"No accepted delivery of '{s}' for {businessDate:yyyy-MM-dd}.")).ToList();
    }

    public async Task<DeliverySnapshot?> GetAsync(string deliveryId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, source_name, business_date, state, fingerprint_sha256, supersedes_delivery_id, accepted_record_count,
                   rejected_record_count, total_row_count, ingested_at, source_format, legal_entity_id, run_id, version
            FROM source_deliveries WHERE id=@id
            """);
        cmd.Parameters.AddWithValue("id", deliveryId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DeliverySnapshot(r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), DeliveryStates.Parse(r.GetString(3)),
            r.GetString(4).Trim(), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8),
            r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
            WireEnum.TryParse<SourceFormat>(r.GetString(10), out var f) ? f : SourceFormat.Csv,
            r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetInt32(13));
    }
}
