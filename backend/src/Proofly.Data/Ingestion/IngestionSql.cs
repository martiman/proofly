using Npgsql;

namespace Proofly.Data.Ingestion;

internal static class IngestionSql
{
    /// <summary>Serialises registration and finalisation per legal entity, source and business date for the life of the transaction.</summary>
    public static async Task LockSourceDateAsync(NpgsqlTransaction tx, string? legalEntityId, string source, DateOnly date, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", tx.Connection, tx);
        c.Parameters.AddWithValue("k", $"delivery|{legalEntityId}|{source}|{date:yyyy-MM-dd}");
        await c.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] ps)
    {
        await using var c = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var o = await c.ExecuteScalarAsync(ct);
        return o is null or DBNull ? default : (T)o;
    }

    public static async Task InsertFailureAsync(NpgsqlTransaction tx, string deliveryId, DeliveryFailure f, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(
            "INSERT INTO delivery_rejections (delivery_id, source_row_id, reason_code, reason) VALUES (@d, NULL, @c, @r)", tx.Connection, tx);
        c.Parameters.AddWithValue("d", deliveryId);
        c.Parameters.AddWithValue("c", f.ReasonCode);
        c.Parameters.AddWithValue("r", f.Message);
        await c.ExecuteNonQueryAsync(ct);
    }
}
