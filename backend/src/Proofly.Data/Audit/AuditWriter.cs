using Npgsql;
using NpgsqlTypes;
using Proofly.Domain.Enums;

namespace Proofly.Data.Audit;

/// <summary>
/// Appends audit events INSIDE the caller's transaction, so the business change and its audit event commit or roll
/// back together.
///
/// Unit-of-work pattern for repositories:
/// <code>
/// await using var conn = await dataSource.OpenConnectionAsync(ct);
/// await using var tx = await conn.BeginTransactionAsync(ct);   // default READ COMMITTED
/// ... business writes using conn/tx ...
/// await audit.AppendAsync(tx, new AuditEventInput(...), ct);   // same tx; throws on invalid input
/// await tx.CommitAsync(ct);                                    // or dispose = rollback of both
/// </code>
/// The writer never opens its own connection or commits. Do not use REPEATABLE READ/SERIALIZABLE for this
/// transaction (the chain-head row lock relies on READ COMMITTED re-reads).
/// </summary>
public interface IAuditWriter
{
    Task<AuditEventRecord> AppendAsync(NpgsqlTransaction tx, AuditEventInput input, CancellationToken ct = default);

    /// <summary>
    /// Appends many events, in order, in the caller's transaction. Same chain, hashing and validation as <see cref="AppendAsync"/>;
    /// the default implementation appends one by one, <see cref="AuditWriter"/> does it in a few round trips per chain.
    /// </summary>
    async Task AppendManyAsync(NpgsqlTransaction tx, IReadOnlyList<AuditEventInput> inputs, CancellationToken ct = default)
    {
        foreach (var i in inputs) await AppendAsync(tx, i, ct);
    }
}

/// <summary>
/// Chain design: one hash chain per chain id (legal entity id, or "*" for global). Concurrent writers are serialised
/// by a row lock on that chain's row in audit_chain_head (SELECT ... FOR UPDATE) held until the business transaction
/// ends; the next sequence and previous hash are read under the lock, so the chain has no gaps or forks, and a
/// rolled-back transaction releases the lock and leaves no sequence hole. Cost: audited transactions on the same
/// chain serialise, acceptable for this write volume.
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    public async Task<AuditEventRecord> AppendAsync(NpgsqlTransaction tx, AuditEventInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(input);
        if (tx.Connection is null) throw new InvalidOperationException("Transaction is already completed.");
        Validate(input);
        var conn = tx.Connection;
        var actorType = WireNames.ToUpperSnake(input.Actor.Type);

        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO audit_chain_head (chain_id, last_seq, last_hash) VALUES (@c, 0, NULL) ON CONFLICT DO NOTHING", conn, tx))
        {
            ensure.Parameters.AddWithValue("c", input.ChainId);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        long lastSeq; string? lastHash; DateTimeOffset now;
        await using (var lck = new NpgsqlCommand(
            "SELECT last_seq, last_hash, clock_timestamp() FROM audit_chain_head WHERE chain_id = @c FOR UPDATE", conn, tx))
        {
            lck.Parameters.AddWithValue("c", input.ChainId);
            await using var r = await lck.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            lastSeq = r.GetInt64(0);
            lastHash = r.IsDBNull(1) ? null : r.GetString(1);
            now = AuditCanonical.TruncateToMicros(r.GetFieldValue<DateTimeOffset>(2));
        }

        var seq = lastSeq + 1;
        var before = input.BeforeJson is null ? null : AuditCanonical.CanonicalJson(input.BeforeJson);
        var after = input.AfterJson is null ? null : AuditCanonical.CanonicalJson(input.AfterJson);
        var hash = AuditCanonical.Sha256Hex(AuditCanonical.Canonicalize(
            input.ChainId, seq, now, input.Type, actorType, input.Actor.Id, input.Actor.Name, input.SubjectKind,
            input.SubjectId, input.RunId, input.RuleVersion, input.Reason, input.Summary, input.RequestId,
            before, after, lastHash));

        long id;
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO audit_events (occurred_at, event_type, actor_type, actor_id, actor_name, subject_kind, subject_id,
              run_id, rule_version, reason, summary, request_id, before, after, chain_id, chain_seq, previous_hash, hash)
            VALUES (@at, @type, @atype, @aid, @aname, @sk, @sid, @run, @rv, @reason, @sum, @req, @before, @after, @c, @seq, @prev, @hash)
            RETURNING id
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, now);
            ins.Parameters.AddWithValue("type", input.Type);
            ins.Parameters.AddWithValue("atype", actorType);
            ins.Parameters.AddWithValue("aid", (object?)input.Actor.Id ?? DBNull.Value);
            ins.Parameters.AddWithValue("aname", input.Actor.Name);
            ins.Parameters.AddWithValue("sk", input.SubjectKind);
            ins.Parameters.AddWithValue("sid", input.SubjectId);
            ins.Parameters.AddWithValue("run", (object?)input.RunId ?? DBNull.Value);
            ins.Parameters.AddWithValue("rv", (object?)input.RuleVersion ?? DBNull.Value);
            ins.Parameters.AddWithValue("reason", (object?)input.Reason ?? DBNull.Value);
            ins.Parameters.AddWithValue("sum", input.Summary);
            ins.Parameters.AddWithValue("req", (object?)input.RequestId ?? DBNull.Value);
            ins.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Jsonb) { Value = (object?)before ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb) { Value = (object?)after ?? DBNull.Value });
            ins.Parameters.AddWithValue("c", input.ChainId);
            ins.Parameters.AddWithValue("seq", seq);
            ins.Parameters.Add(new NpgsqlParameter("prev", NpgsqlDbType.Char) { Value = (object?)lastHash ?? DBNull.Value });
            ins.Parameters.AddWithValue("hash", hash);
            id = (long)(await ins.ExecuteScalarAsync(ct))!;
        }

        await using (var upd = new NpgsqlCommand(
            "UPDATE audit_chain_head SET last_seq = @s, last_hash = @h WHERE chain_id = @c", conn, tx))
        {
            upd.Parameters.AddWithValue("s", seq);
            upd.Parameters.AddWithValue("h", hash);
            upd.Parameters.AddWithValue("c", input.ChainId);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return new AuditEventRecord(id, now, input.Type, input.Actor.Type, input.Actor.Id, input.Actor.Name,
            input.SubjectKind, input.SubjectId, input.RunId, input.Summary, input.Reason, input.RuleVersion,
            input.RequestId, before, after, input.ChainId, seq, lastHash, hash);
    }

    private const int BulkChunk = 1000;

    /// <summary>
    /// Bulk append: the chain head of each chain is locked and read once, the hash chain is computed in memory in input order, rows
    /// are inserted with one statement per chunk and the head is updated once per chain. Timestamps are the database clock at the
    /// lock plus the elapsed monotonic time, so they are non-decreasing in chain order.
    /// </summary>
    public async Task AppendManyAsync(NpgsqlTransaction tx, IReadOnlyList<AuditEventInput> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return;
        if (tx.Connection is null) throw new InvalidOperationException("Transaction is already completed.");
        foreach (var i in inputs) Validate(i);
        var conn = tx.Connection;

        var heads = new Dictionary<string, (long Seq, string? Hash, DateTimeOffset Base, System.Diagnostics.Stopwatch Clock)>(StringComparer.Ordinal);
        foreach (var chain in inputs.Select(i => i.ChainId).Distinct(StringComparer.Ordinal))
        {
            await using (var ensure = new NpgsqlCommand(
                "INSERT INTO audit_chain_head (chain_id, last_seq, last_hash) VALUES (@c, 0, NULL) ON CONFLICT DO NOTHING", conn, tx))
            {
                ensure.Parameters.AddWithValue("c", chain);
                await ensure.ExecuteNonQueryAsync(ct);
            }
            await using var lck = new NpgsqlCommand(
                "SELECT last_seq, last_hash, clock_timestamp() FROM audit_chain_head WHERE chain_id = @c FOR UPDATE", conn, tx);
            lck.Parameters.AddWithValue("c", chain);
            await using var r = await lck.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            heads[chain] = (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), AuditCanonical.TruncateToMicros(r.GetFieldValue<DateTimeOffset>(2)),
                System.Diagnostics.Stopwatch.StartNew());
        }

        var rows = new List<(AuditEventInput In, string ActorType, DateTimeOffset At, string? Before, string? After, long Seq, string? Prev, string Hash)>(inputs.Count);
        foreach (var input in inputs)
        {
            var h = heads[input.ChainId];
            var actorType = WireNames.ToUpperSnake(input.Actor.Type);
            var at = AuditCanonical.TruncateToMicros(h.Base + h.Clock.Elapsed);
            var seq = h.Seq + 1;
            var before = input.BeforeJson is null ? null : AuditCanonical.CanonicalJson(input.BeforeJson);
            var after = input.AfterJson is null ? null : AuditCanonical.CanonicalJson(input.AfterJson);
            var hash = AuditCanonical.Sha256Hex(AuditCanonical.Canonicalize(
                input.ChainId, seq, at, input.Type, actorType, input.Actor.Id, input.Actor.Name, input.SubjectKind,
                input.SubjectId, input.RunId, input.RuleVersion, input.Reason, input.Summary, input.RequestId, before, after, h.Hash));
            rows.Add((input, actorType, at, before, after, seq, h.Hash, hash));
            heads[input.ChainId] = (seq, hash, h.Base, h.Clock);
        }

        foreach (var chunk in rows.Chunk(BulkChunk))
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO audit_events (occurred_at, event_type, actor_type, actor_id, actor_name, subject_kind, subject_id,
                  run_id, rule_version, reason, summary, request_id, before, after, chain_id, chain_seq, previous_hash, hash)
                SELECT t.at, t.type, t.atype, t.aid, t.aname, t.sk, t.sid, t.run, t.rv, t.reason, t.sum, t.req, t.before::jsonb, t.after::jsonb,
                       t.c, t.seq, t.prev, t.hash
                FROM unnest(@at, @type, @atype, @aid, @aname, @sk, @sid, @run, @rv, @reason, @sum, @req, @before, @after, @c, @seq, @prev, @hash)
                     WITH ORDINALITY AS t(at, type, atype, aid, aname, sk, sid, run, rv, reason, sum, req, before, after, c, seq, prev, hash, ord)
                ORDER BY t.ord
                """, conn, tx);
            void A(string n, NpgsqlDbType t, Array v) => ins.Parameters.Add(new NpgsqlParameter(n, NpgsqlDbType.Array | t) { Value = v });
            A("at", NpgsqlDbType.TimestampTz, chunk.Select(r => r.At).ToArray());
            A("type", NpgsqlDbType.Text, chunk.Select(r => r.In.Type).ToArray());
            A("atype", NpgsqlDbType.Text, chunk.Select(r => r.ActorType).ToArray());
            A("aid", NpgsqlDbType.Text, chunk.Select(r => r.In.Actor.Id).ToArray());
            A("aname", NpgsqlDbType.Text, chunk.Select(r => r.In.Actor.Name).ToArray());
            A("sk", NpgsqlDbType.Text, chunk.Select(r => r.In.SubjectKind).ToArray());
            A("sid", NpgsqlDbType.Text, chunk.Select(r => r.In.SubjectId).ToArray());
            A("run", NpgsqlDbType.Text, chunk.Select(r => r.In.RunId).ToArray());
            A("rv", NpgsqlDbType.Text, chunk.Select(r => r.In.RuleVersion).ToArray());
            A("reason", NpgsqlDbType.Text, chunk.Select(r => r.In.Reason).ToArray());
            A("sum", NpgsqlDbType.Text, chunk.Select(r => r.In.Summary).ToArray());
            A("req", NpgsqlDbType.Text, chunk.Select(r => r.In.RequestId).ToArray());
            A("before", NpgsqlDbType.Text, chunk.Select(r => r.Before).ToArray());
            A("after", NpgsqlDbType.Text, chunk.Select(r => r.After).ToArray());
            A("c", NpgsqlDbType.Text, chunk.Select(r => r.In.ChainId).ToArray());
            A("seq", NpgsqlDbType.Bigint, chunk.Select(r => r.Seq).ToArray());
            A("prev", NpgsqlDbType.Text, chunk.Select(r => r.Prev).ToArray());
            A("hash", NpgsqlDbType.Text, chunk.Select(r => r.Hash).ToArray());
            await ins.ExecuteNonQueryAsync(ct);
        }

        foreach (var (chain, h) in heads)
        {
            await using var upd = new NpgsqlCommand("UPDATE audit_chain_head SET last_seq = @s, last_hash = @h WHERE chain_id = @c", conn, tx);
            upd.Parameters.AddWithValue("s", h.Seq);
            upd.Parameters.AddWithValue("h", h.Hash!);
            upd.Parameters.AddWithValue("c", chain);
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void Validate(AuditEventInput i)
    {
        if (!AuditVocabulary.EventTypes.Contains(i.Type)) throw new ArgumentException($"Unknown audit event type '{i.Type}'.");
        if (!AuditVocabulary.SubjectKinds.Contains(i.SubjectKind)) throw new ArgumentException($"Unknown audit subject kind '{i.SubjectKind}'.");
        if (string.IsNullOrWhiteSpace(i.SubjectId)) throw new ArgumentException("SubjectId is required.");
        if (string.IsNullOrWhiteSpace(i.Summary)) throw new ArgumentException("Summary is required.");
        if (string.IsNullOrWhiteSpace(i.Actor.Name)) throw new ArgumentException("Actor name is required.");
        if (i.Actor.Type == ActorType.User && string.IsNullOrWhiteSpace(i.Actor.Id)) throw new ArgumentException("A USER actor needs an id.");
        if (string.IsNullOrWhiteSpace(i.ChainId)) throw new ArgumentException("ChainId is required.");
        if (i.Reason is not null && i.Reason.Trim().Length < 3) throw new ArgumentException("Reason must be at least 3 characters.");
    }
}

/// <summary>Allowed values, mirroring AuditEventType and AuditSubjectKind in openapi.yaml.</summary>
public static class AuditVocabulary
{
    public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "DELIVERY_RECEIVED", "RECORD_REJECTED", "DELIVERY_INGESTION_RETRIED", "DELIVERY_SUPERSEDED", "RUN_STARTED",
        "RUN_REPROCESS_REQUESTED", "CONTROL_EXECUTED", "EXCEPTION_CREATED", "EXCEPTION_ASSIGNED", "NOTE_ADDED",
        "EVIDENCE_REQUESTED", "RESOLUTION_PROPOSED", "RESOLUTION_DECIDED", "EXCEPTION_REOPENED",
        "MANUAL_MATCH_PROPOSED", "MANUAL_MATCH_DECIDED", "ADJUSTMENT_PROPOSED", "ADJUSTMENT_DECIDED", "SIGN_OFF_SUBMITTED",
        "SIGN_OFF_RETURNED", "RUN_SIGNED_OFF", "EVIDENCE_PACK_GENERATED", "EVIDENCE_PACK_DOWNLOADED",
        "CONFIG_CHANGE_PROPOSED", "CONFIG_CHANGE_DECIDED", "ACCESS_CHANGED",
        "ADJUSTMENT_EXPIRED", "RUN_PROCESSED", "SIGN_OFF_INVALIDATED", "EVIDENCE_ACCESS_DENIED",
    };

    public static readonly IReadOnlySet<string> SubjectKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "RUN", "DELIVERY", "RECORD", "EXCEPTION", "EVIDENCE_PACK", "ADJUSTMENT", "SIGN_OFF", "CONFIG", "MATCH_GROUP",
    };
}
