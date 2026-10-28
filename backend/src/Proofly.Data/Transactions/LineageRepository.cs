using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Proofly.Domain.Ids;

namespace Proofly.Data.Transactions;

/// <summary>Subject kinds of getLineage (contract parameter <c>subjectKind</c>).</summary>
public static class LineageSubjectKinds
{
    public const string Exception = "EXCEPTION", Transaction = "TRANSACTION", Record = "RECORD", ControlResult = "CONTROL_RESULT";
}

/// <summary>
/// The resolved subject of a lineage request: the run and legal entity it belongs to (for scope and permission checks by the caller),
/// plus what to draw (a transaction, an exception, or a plain record).
/// </summary>
public sealed record LineageSubject(string Kind, string Id, string RunId, string LegalEntityId, string? TransactionId, string? ExceptionId,
    string? DeliveryId, string? RecordSource, string? RecordRowId);

public interface ILineageRepository
{
    /// <summary>Resolves the subject to its run and entity; null when it does not exist, the id is malformed for the kind, or it is not part of <paramref name="runId"/>.</summary>
    Task<LineageSubject?> FindSubjectAsync(string subjectKind, string subjectId, string? runId, CancellationToken ct = default);

    /// <summary>The lineage graph of a subject found by <see cref="FindSubjectAsync"/>. Transactions use the same logic as getTransactionLineage.</summary>
    Task<TransactionLineage?> GetLineageAsync(LineageSubject subject, CancellationToken ct = default);
}

public sealed class LineageRepository(NpgsqlDataSource db, ITransactionReadRepository transactions) : ILineageRepository
{
    // Manifest source name -> (record table, transaction id prefix; null when the source holds no transactions).
    private static readonly Dictionary<string, (string Table, string? TxPrefix)> Sources = new(StringComparer.Ordinal)
    {
        ["transfer_instructions"] = ("transfer_instructions", "TRF"),
        ["direct_debit_events"] = ("direct_debit_events", "DD"),
        ["card_transactions"] = ("card_events", "CRD"),
        ["banking_transactions"] = ("bank_transactions", "BNK"),
        ["internal_ledger_entries"] = ("ledger_entries", "LED"),
        ["client_account_balances"] = ("account_balances", null),
        ["safeguarding_balances"] = ("safeguarding_balances", null),
        ["card_settlements"] = ("card_settlements", null),
    };

    private const string ExceptionPrefix = "EXC-";

    public async Task<LineageSubject?> FindSubjectAsync(string subjectKind, string subjectId, string? runId, CancellationToken ct = default)
    {
        switch (subjectKind)
        {
            case LineageSubjectKinds.Transaction:
            {
                if (!TransactionIds.TryParse(subjectId, out var table, out var pk)) { return null; }
                var home = await RecordHomeAsync(table, pk, ct);
                return home is null || (runId is not null && home.Value.RunId != runId) ? null
                    : new LineageSubject(subjectKind, subjectId, home.Value.RunId, home.Value.LegalEntityId, subjectId, null, home.Value.DeliveryId, null, null);
            }
            case LineageSubjectKinds.Record:
            {
                var parts = subjectId.Split(':', 2);
                if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pk) || !Sources.TryGetValue(parts[0], out var src)) { return null; }
                var home = await RecordHomeAsync(src.Table, pk, ct);
                if (home is null || (runId is not null && home.Value.RunId != runId)) { return null; }
                var txId = src.TxPrefix is null ? null : src.TxPrefix + "-" + pk.ToString(CultureInfo.InvariantCulture);
                return new LineageSubject(subjectKind, subjectId, home.Value.RunId, home.Value.LegalEntityId, txId, null, home.Value.DeliveryId, parts[0], parts[1]);
            }
            case LineageSubjectKinds.Exception:
            case LineageSubjectKinds.ControlResult:
            {
                var id = subjectId.StartsWith(ExceptionPrefix, StringComparison.Ordinal) ? subjectId[ExceptionPrefix.Length..] : subjectId;
                await using var cmd = db.CreateCommand("SELECT run_id, legal_entity_id, delivery_id FROM exceptions WHERE id = @id");
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) { return null; }
                if (runId is not null && r.GetString(0) != runId) { return null; }
                return new LineageSubject(subjectKind, subjectId, r.GetString(0), r.GetString(1), null, id, r.IsDBNull(2) ? null : r.GetString(2), null, null);
            }
            default: return null;
        }
    }

    /// <summary>Run, legal entity and delivery of a canonical record (the run the record's delivery is currently linked to).</summary>
    private async Task<(string RunId, string LegalEntityId, string DeliveryId)?> RecordHomeAsync(string table, long pk, CancellationToken ct)
    {
        if (!Sources.Values.Any(s => s.Table == table)) { return null; } // the table name is interpolated: whitelist only
        await using var cmd = db.CreateCommand($"""
            SELECT d.run_id, r.legal_entity_id, d.id FROM {table} t
              JOIN source_deliveries d ON d.id = t.delivery_id
              JOIN runs r ON r.id = d.run_id
             WHERE t.id = @pk
            """);
        cmd.Parameters.Add(new NpgsqlParameter("pk", NpgsqlDbType.Bigint) { Value = pk });
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.GetString(1), rd.GetString(2)) : null;
    }

    public async Task<TransactionLineage?> GetLineageAsync(LineageSubject s, CancellationToken ct = default)
    {
        if (s.TransactionId is not null)
        {
            return await transactions.GetLineageAsync(s.RunId, s.TransactionId, ct);
        }

        if (s.ExceptionId is { } exId)
        {
            // The first transaction the exception is attached to carries the graph (its lineage already contains the EXC node).
            var tx = await FirstTransactionOfExceptionAsync(s.RunId, exId, ct);
            if (tx is not null && await transactions.GetLineageAsync(s.RunId, tx, ct) is { } l) { return l with { TransactionId = s.Id }; }

            var nodes = new List<LineageNode> { new(ExceptionPrefix + exId, "CONTROL_RESULT", exId, "exceptions", exId, null, null, null) };
            var edges = new List<LineageEdge>();
            var deliveries = new List<LineageDelivery>();
            if (s.DeliveryId is { } did && await DeliveryAsync(s.RunId, did, ct) is { } d)
            {
                nodes.Add(new LineageNode("DLV-" + did, "DELIVERY", did, "source_deliveries", did, null, null, null));
                edges.Add(new LineageEdge("DLV-" + did, ExceptionPrefix + exId, "RAISED_FROM"));
                deliveries.Add(d);
            }
            return new TransactionLineage(s.Id, nodes, edges, deliveries);
        }

        if (s.RecordSource is { } src && s.RecordRowId is { } pk && s.DeliveryId is { } rdid && await DeliveryAsync(s.RunId, rdid, ct) is { } del)
        {
            var node = new LineageNode(s.Id, "BALANCE", s.Id, src, pk, s.Id, null, null);
            var dnode = new LineageNode("DLV-" + rdid, "DELIVERY", rdid, "source_deliveries", rdid, null, null, null);
            return new TransactionLineage(s.Id, [node, dnode], [new LineageEdge(dnode.Id, node.Id, "DELIVERED")], [del]);
        }
        return null;
    }

    private async Task<string?> FirstTransactionOfExceptionAsync(string runId, string exceptionId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("WITH " + TransactionSql.TxCte + " SELECT id FROM tx WHERE @ex = ANY(exception_ids) ORDER BY ts, id LIMIT 1");
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("ex", exceptionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task<LineageDelivery?> DeliveryAsync(string runId, string deliveryId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, fingerprint_sha256, schema_version FROM source_deliveries WHERE id = @d AND run_id = @r");
        cmd.Parameters.AddWithValue("d", deliveryId);
        cmd.Parameters.AddWithValue("r", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new LineageDelivery(r.GetString(0), r.GetString(1), r.GetString(2), null) : null;
    }
}
