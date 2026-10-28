using System.Globalization;
using System.Text.Json;
using Npgsql;
using Proofly.Connectors.Abstractions;
using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Ingestion;

/// <summary>
/// Ingests one registered delivery: reads the stored evidence, validates the schema, parses every row through the
/// adapter, applies the framework checks and persists the canonical records and the rejected rows with the delivery
/// state and audit events in ONE transaction. Rows are accepted or rejected, never repaired; rejected rows are kept in
/// <c>delivery_rejections</c> and counted (<c>total_row_count = accepted + rejected</c> for QUALIFIED/RECEIVED).
///
/// Framework row checks (after the adapter): unknown or mismatching legal entity, amount precision above 6 decimals,
/// repeated source_row_id / natural key (DUPLICATE_RECORD, first occurrence kept).
/// Delivery checks: declared record count equals ALL data rows (accepted + rejected); declared EUR/CZK totals equal
/// the sum of every row's control amount by currency (rejected rows included when their amount was readable).
///
/// Outcome: file-level failure (evidence, schema, unreadable) = REJECTED. Count mismatch and value mismatch follow
/// <see cref="SourcePolicy"/> (default: count REJECTED, value QUALIFIED). Row rejections = QUALIFIED. Otherwise
/// RECEIVED (the contract has no ACCEPTED state; RECEIVED with ingested_at set means ingested cleanly). A REJECTED
/// delivery stores no canonical records. A correction that ends RECEIVED/QUALIFIED marks the delivery it corrects
/// SUPERSEDED. Idempotent: an already ingested delivery is returned unchanged.
/// </summary>
public sealed class DeliveryIngestor(
    NpgsqlDataSource dataSource, IAuditWriter audit, EvidenceStore evidence, ISourceAdapterRegistry adapters, IngestionOptions options)
{
    private const int ExcerptMax = 500;

    private sealed record Row(string Id, string Source, DateOnly Date, string SchemaVersion, string FileName, string Fingerprint,
        SourceFormat Format, string? LegalEntityId, string? RunId, int? DeclaredCount, decimal? DeclaredEur, decimal? DeclaredCzk,
        string? Supersedes, string State, DateTimeOffset? IngestedAt);

    private sealed class Analysis
    {
        public List<ISourceRecord> Accepted { get; } = [];
        public List<RowRejection> RowRejections { get; } = [];
        public List<DeliveryFailure> FileFailures { get; } = [];
        public List<DeliveryFailure> Rejecting { get; } = [];
        public List<DeliveryFailure> Qualifying { get; } = [];
        public int TotalRows;
        public Dictionary<string, decimal> Totals { get; } = [];
    }

    public async Task<IngestionOutcome> IngestAsync(string deliveryId, CancellationToken ct = default)
    {
        var d = await LoadAsync(deliveryId, ct) ?? throw new PermanentJobException($"Delivery {deliveryId} does not exist.");
        if (d.IngestedAt is not null || d.State != "RECEIVED") return Already(d);

        var a = await AnalyseAsync(d, ct);
        return await FinaliseAsync(d, a, ct);
    }

    private static IngestionOutcome Already(Row d) =>
        new(d.Id, DeliveryStates.Parse(d.State), true, 0, 0, []);

    private async Task<Row?> LoadAsync(string id, CancellationToken ct)
    {
        await using var c = dataSource.CreateCommand("""
            SELECT id, source_name, business_date, schema_version, file_name, fingerprint_sha256, source_format, legal_entity_id, run_id,
                   declared_record_count, declared_total_eur, declared_total_czk, supersedes_delivery_id, state, ingested_at
            FROM source_deliveries WHERE id=@id
            """);
        c.Parameters.AddWithValue("id", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new Row(r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetString(3), r.GetString(4), r.GetString(5).Trim(),
            WireEnum.TryParse<SourceFormat>(r.GetString(6), out var f) ? f : SourceFormat.Csv, S(7), S(8),
            r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetDecimal(11),
            S(12), r.GetString(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14));
    }

    private async Task<Analysis> AnalyseAsync(Row d, CancellationToken ct)
    {
        var a = new Analysis();
        var adapter = adapters.Find(d.Source, d.Format);
        if (adapter is null)
        {
            a.FileFailures.Add(new(ReasonCodes.SchemaUnsupported, $"No adapter for source '{d.Source}' and format {d.Format}."));
            return a;
        }
        if (!RecordTables.Supports(adapter.RecordType))
        {
            a.FileFailures.Add(new(ReasonCodes.SchemaUnsupported, $"Adapter record type {adapter.RecordType.Name} cannot be persisted."));
            return a;
        }
        if (!await evidence.VerifyAsync(d.Fingerprint, ct))
        {
            a.FileFailures.Add(new(ReasonCodes.EvidenceMissing, "Stored evidence is missing or does not match the delivery fingerprint."));
            return a;
        }

        var descriptor = new SourceDescriptor(d.Source, d.Format, d.SchemaVersion, d.Id, d.Date, d.FileName, d.LegalEntityId);
        await using (var s = evidence.OpenRead(d.Fingerprint))
        {
            SchemaValidation v;
            try { v = await adapter.ValidateSchemaAsync(s, descriptor, ct); }
            catch (SourceFormatException e) { v = SchemaValidation.Fail(e.Message, ReasonCodes.FileUnreadable); }
            if (!v.IsValid)
            {
                a.FileFailures.Add(new(v.ReasonCode ?? ReasonCodes.SchemaUnsupported, v.Message ?? "Schema validation failed."));
                return a;
            }
        }

        var entities = await LegalEntitiesAsync(ct);
        var rowIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await using var s = evidence.OpenRead(d.Fingerprint);
            await foreach (var res in adapter.ParseRecordsAsync(s, descriptor, ct))
            {
                a.TotalRows++;
                if (res.Record is null)
                {
                    var rej = res.Rejection ?? new RowRejection(ReasonCodes.InvalidValue, "Adapter returned neither record nor rejection.", res.RowNumber);
                    a.RowRejections.Add(rej);
                    Add(a, rej.ControlAmount);
                    continue;
                }
                var rec = res.Record;
                var fw = FrameworkRejection(rec, res.RowNumber, d, entities, rowIds, keys);
                if (fw is not null) { a.RowRejections.Add(fw); Add(a, rec.ControlAmount); continue; }
                a.Accepted.Add(rec);
                Add(a, rec.ControlAmount);
            }
        }
        catch (SourceFormatException e)
        {
            a.Accepted.Clear(); a.RowRejections.Clear(); a.Totals.Clear(); a.TotalRows = 0;
            a.FileFailures.Add(new(ReasonCodes.FileUnreadable, e.Message));
            return a;
        }

        var policy = options.PolicyFor(d.Source);
        void Classify(MismatchOutcome o, DeliveryFailure f) => (o == MismatchOutcome.Reject ? a.Rejecting : a.Qualifying).Add(f);
        if (d.DeclaredCount is { } n && n != a.TotalRows)
            Classify(policy.CountMismatch, new(ReasonCodes.ControlCountMismatch, $"Manifest declares {n} records but file contains {a.TotalRows}."));
        foreach (var (cur, declared) in new[] { ("EUR", d.DeclaredEur), ("CZK", d.DeclaredCzk) })
        {
            if (declared is not { } dec) continue;
            var actual = a.Totals.GetValueOrDefault(cur);
            if (actual != dec)
                Classify(policy.ValueMismatch, new(ReasonCodes.ControlValueMismatch,
                    $"Manifest declares {Fmt(dec)} {cur} but the file sums to {Fmt(actual)} {cur}."));
        }
        return a;
    }

    private static string Fmt(decimal v) => v.ToString("0.00####", CultureInfo.InvariantCulture);

    private static void Add(Analysis a, Money? m)
    {
        if (m is { } x) a.Totals[x.Currency] = a.Totals.GetValueOrDefault(x.Currency) + x.Amount;
    }

    private static RowRejection? FrameworkRejection(ISourceRecord rec, int rowNumber, Row d, HashSet<string> entities,
        Dictionary<string, int> rowIds, Dictionary<string, int> keys)
    {
        RowRejection R(string code, string msg, string? field = null) => new(code, msg, rowNumber, null, rec.SourceRowId, field, rec.ControlAmount);
        if (rec.LegalEntityId is { } le)
        {
            if (!entities.Contains(le)) return R(ReasonCodes.InvalidValue, $"legal_entity_id '{le}' is not a known legal entity.", "legal_entity_id");
            if (d.LegalEntityId is { } dle && dle != le) return R(ReasonCodes.InvalidValue, $"legal_entity_id '{le}' differs from the delivery's legal entity '{dle}'.", "legal_entity_id");
        }
        foreach (var m in rec.Amounts)
        {
            if (decimal.Round(m.Amount, 6) != m.Amount) return R(ReasonCodes.InvalidValue, $"Amount {m.Amount.ToString(CultureInfo.InvariantCulture)} has more than 6 decimal places.", "amount");
        }
        if (rowIds.TryGetValue(rec.SourceRowId, out var firstRow))
            return R(ReasonCodes.DuplicateRecord, $"source_row_id '{rec.SourceRowId}' already used by row {firstRow}.", "source_row_id");
        if (keys.TryGetValue(rec.NaturalKey, out var firstKey))
            return R(ReasonCodes.DuplicateRecord, $"Record '{rec.NaturalKey}' duplicates row {firstKey}.");
        rowIds[rec.SourceRowId] = rowNumber;
        keys[rec.NaturalKey] = rowNumber;
        return null;
    }

    private async Task<HashSet<string>> LegalEntitiesAsync(CancellationToken ct)
    {
        await using var c = dataSource.CreateCommand("SELECT id FROM legal_entities");
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    private async Task<IngestionOutcome> FinaliseAsync(Row d, Analysis a, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await IngestionSql.LockSourceDateAsync(tx, d.Source, d.Date, ct);

        var current = await IngestionSql.ScalarAsync<string>(tx,
            "SELECT state FROM source_deliveries WHERE id=@id AND ingested_at IS NULL FOR UPDATE", ct, ("id", d.Id));
        if (current != "RECEIVED")
        {
            var now = (await LoadAsync(d.Id, ct))!;
            return Already(now);
        }

        var failures = new List<DeliveryFailure>(a.FileFailures);
        failures.AddRange(a.Rejecting);
        failures.AddRange(a.Qualifying);
        var rejected = a.FileFailures.Count > 0 || a.Rejecting.Count > 0;

        string? supersededTarget = null;
        if (!rejected && d.Supersedes is { } target)
        {
            var ts = await IngestionSql.ScalarAsync<string>(tx, "SELECT state FROM source_deliveries WHERE id=@id FOR UPDATE", ct, ("id", target));
            if (ts is "RECEIVED" or "QUALIFIED") supersededTarget = target;
            else
            {
                rejected = true;
                failures.Add(new(ReasonCodes.CorrectionLinkMissing, $"Corrected delivery {target} is no longer current (state {ts ?? "unknown"})."));
            }
        }

        var state = rejected ? DeliveryState.Rejected
            : (a.Qualifying.Count > 0 || a.RowRejections.Count > 0 ? DeliveryState.Qualified : DeliveryState.Received);
        var accepted = rejected ? 0 : a.Accepted.Count;

        if (!rejected) await RecordTables.InsertAsync(tx, d.Id, a.Accepted, ct);
        foreach (var r in a.RowRejections) await InsertRowRejectionAsync(tx, d.Id, r, ct);
        // Delivery-level failures are persisted unless registration already stored the same facts (none are stored then).
        foreach (var f in failures) await IngestionSql.InsertFailureAsync(tx, d.Id, f, ct);

        await using (var up = new NpgsqlCommand("""
            UPDATE source_deliveries SET state=@s, accepted_record_count=@a, rejected_record_count=@r, total_row_count=@t,
              actual_total_eur=@eur, actual_total_czk=@czk, ingested_at=now() WHERE id=@id
            """, conn, tx))
        {
            up.Parameters.AddWithValue("s", DeliveryStates.Wire(state));
            up.Parameters.AddWithValue("a", accepted);
            up.Parameters.AddWithValue("r", a.RowRejections.Count);
            up.Parameters.AddWithValue("t", a.TotalRows);
            up.Parameters.AddWithValue("eur", a.Totals.TryGetValue("EUR", out var e) ? e : DBNull.Value);
            up.Parameters.AddWithValue("czk", a.Totals.TryGetValue("CZK", out var z) ? z : DBNull.Value);
            up.Parameters.AddWithValue("id", d.Id);
            await up.ExecuteNonQueryAsync(ct);
        }

        if (supersededTarget is not null)
        {
            await IngestionSql.ScalarAsync<int>(tx, "UPDATE source_deliveries SET state='SUPERSEDED' WHERE id=@id RETURNING 1", ct, ("id", supersededTarget));
            await audit.AppendAsync(tx, new AuditEventInput("DELIVERY_SUPERSEDED", DeliveryRegistry.System, "DELIVERY", supersededTarget,
                $"Delivery {supersededTarget} superseded by correction {d.Id}", Reason: $"Corrected by delivery {d.Id}", RunId: d.RunId,
                AfterJson: JsonSerializer.Serialize(new { state = "SUPERSEDED", supersededBy = d.Id })), ct);
        }

        await audit.AppendAsync(tx, new AuditEventInput("DELIVERY_RECEIVED", DeliveryRegistry.System, "DELIVERY", d.Id,
            $"Delivery {d.Id} ingested as {DeliveryStates.Wire(state)}: {accepted} accepted, {a.RowRejections.Count} rejected rows of {a.TotalRows}",
            Reason: failures.Count > 0 ? string.Join("; ", failures.Select(f => f.ReasonCode + ": " + f.Message))
                : a.RowRejections.Count > 0 ? "Rows rejected; population qualified" : "All checks passed",
            RunId: d.RunId,
            AfterJson: JsonSerializer.Serialize(new
            {
                state = DeliveryStates.Wire(state), acceptedRecords = accepted, rejectedRecords = a.RowRejections.Count, totalRows = a.TotalRows,
                actualTotalEur = Fmt(a.Totals.GetValueOrDefault("EUR")), actualTotalCzk = Fmt(a.Totals.GetValueOrDefault("CZK")),
                failures = failures.Select(f => f.ReasonCode),
            })), ct);

        foreach (var g in a.RowRejections.GroupBy(r => r.ReasonCode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            await audit.AppendAsync(tx, new AuditEventInput("RECORD_REJECTED", DeliveryRegistry.System, "DELIVERY", d.Id,
                $"{g.Count()} record(s) of {d.Source} rejected: {g.Key}",
                Reason: g.First().Message, RunId: d.RunId,
                AfterJson: JsonSerializer.Serialize(new { reasonCode = g.Key, count = g.Count(), rows = g.Select(r => r.RowNumber).Take(50) })), ct);
        }

        await tx.CommitAsync(ct);
        return new IngestionOutcome(d.Id, state, false, accepted, a.RowRejections.Count, failures);
    }

    private static async Task InsertRowRejectionAsync(NpgsqlTransaction tx, string deliveryId, RowRejection r, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("""
            INSERT INTO delivery_rejections (delivery_id, source_row_id, reason_code, reason, row_number, field, raw_excerpt)
            VALUES (@d, @s, @c, @r, @n, @f, @x)
            """, tx.Connection, tx);
        c.Parameters.AddWithValue("d", deliveryId);
        c.Parameters.AddWithValue("s", (object?)r.SourceRowId ?? DBNull.Value);
        c.Parameters.AddWithValue("c", r.ReasonCode);
        c.Parameters.AddWithValue("r", r.Message);
        c.Parameters.AddWithValue("n", r.RowNumber);
        c.Parameters.AddWithValue("f", (object?)r.Field ?? DBNull.Value);
        c.Parameters.AddWithValue("x", r.RawExcerpt is null ? DBNull.Value : r.RawExcerpt.Length <= ExcerptMax ? r.RawExcerpt : r.RawExcerpt[..ExcerptMax]);
        await c.ExecuteNonQueryAsync(ct);
    }
}
