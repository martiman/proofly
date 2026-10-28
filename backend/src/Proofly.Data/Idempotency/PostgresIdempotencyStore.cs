using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Proofly.Data.Idempotency;

/// <summary>
/// idempotency_keys-backed store. Claiming is one atomic INSERT ... ON CONFLICT DO UPDATE ... WHERE: a row is (re)claimed only
/// when it is expired, or still IN_PROGRESS with an ended lease (the claimant crashed). Exactly one concurrent caller wins.
/// The lease is not renewed: it must exceed the longest expected request (default 5 minutes).
/// </summary>
public sealed class PostgresIdempotencyStore(NpgsqlDataSource db, TimeProvider time, TimeSpan? lease = null) : IIdempotencyStore
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _lease = lease ?? DefaultLease;

    public async Task<IdempotencyBegin> BeginAsync(string scope, string key, string requestHash, TimeSpan ttl, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = time.GetUtcNow();
            await using (var claim = db.CreateCommand("""
                INSERT INTO idempotency_keys AS k (user_id, "key", request_hash, state, created_at, expires_at, lease_expires_at)
                VALUES (@scope, @key, @hash, 'IN_PROGRESS', @now, @exp, @lease)
                ON CONFLICT (user_id, "key") DO UPDATE SET
                  request_hash = EXCLUDED.request_hash, state = 'IN_PROGRESS', response_status = NULL, response_body = NULL,
                  response_bytes = NULL, response_headers = NULL, content_type = NULL, request_id = NULL,
                  created_at = EXCLUDED.created_at, expires_at = EXCLUDED.expires_at, lease_expires_at = EXCLUDED.lease_expires_at
                WHERE k.expires_at <= @now OR (k.state = 'IN_PROGRESS' AND k.lease_expires_at <= @now)
                RETURNING 1
                """))
            {
                claim.Parameters.AddWithValue("scope", scope);
                claim.Parameters.AddWithValue("key", key);
                claim.Parameters.AddWithValue("hash", requestHash);
                claim.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
                claim.Parameters.AddWithValue("exp", NpgsqlDbType.TimestampTz, now + ttl);
                claim.Parameters.AddWithValue("lease", NpgsqlDbType.TimestampTz, now + _lease);
                if (await claim.ExecuteScalarAsync(ct) is not null) { return new IdempotencyBegin(IdempotencyOutcome.Started); }
            }

            await using var read = db.CreateCommand("""
                SELECT request_hash, state, response_status, content_type, response_headers::text, response_bytes, request_id
                FROM idempotency_keys WHERE user_id = @scope AND "key" = @key
                """);
            read.Parameters.AddWithValue("scope", scope);
            read.Parameters.AddWithValue("key", key);
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { continue; } // released between the two statements: claim again
            if (!string.Equals(r.GetString(0).Trim(), requestHash, StringComparison.Ordinal)) { return new IdempotencyBegin(IdempotencyOutcome.Mismatch); }
            if (r.GetString(1) != "COMPLETED") { return new IdempotencyBegin(IdempotencyOutcome.InProgress); }
            var headers = r.IsDBNull(4)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4)) ?? new Dictionary<string, string>();
            return new IdempotencyBegin(IdempotencyOutcome.Replay, new StoredResponse(
                r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), headers, r.IsDBNull(5) ? [] : r.GetFieldValue<byte[]>(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return new IdempotencyBegin(IdempotencyOutcome.InProgress);
    }

    public async Task CompleteAsync(string scope, string key, StoredResponse response, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE idempotency_keys
            SET state = 'COMPLETED', response_status = @status, content_type = @ct, response_headers = @headers, response_bytes = @body, request_id = @req
            WHERE user_id = @scope AND "key" = @key AND state = 'IN_PROGRESS'
            """);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("status", response.StatusCode);
        cmd.Parameters.Add(new NpgsqlParameter("ct", NpgsqlDbType.Text) { Value = (object?)response.ContentType ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(response.Headers) });
        cmd.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Bytea) { Value = response.Body });
        cmd.Parameters.Add(new NpgsqlParameter("req", NpgsqlDbType.Text) { Value = (object?)response.RequestId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReleaseAsync(string scope, string key, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("DELETE FROM idempotency_keys WHERE user_id = @scope AND \"key\" = @key AND state = 'IN_PROGRESS'");
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
