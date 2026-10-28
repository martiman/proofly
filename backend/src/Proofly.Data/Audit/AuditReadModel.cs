using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Proofly.Domain.Enums;

namespace Proofly.Data.Audit;

public interface IAuditVerifier
{
    /// <summary>Recomputes every chain (or one) and reports the first broken link, if any.</summary>
    Task<AuditVerification> VerifyAsync(string? chainId = null, CancellationToken ct = default);
}

public interface IAuditReader
{
    Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct = default);
}

internal static class AuditRows
{
    public const string Columns = """
        id, occurred_at, event_type, actor_type, actor_id, actor_name, subject_kind, subject_id, run_id, summary,
        reason, rule_version, request_id, before::text, after::text, chain_id, chain_seq, previous_hash, hash
        """;

    public static ActorType ParseActor(string s) => s switch
    {
        "USER" => ActorType.User,
        "SYSTEM" => ActorType.System,
        "SERVICE_ACCOUNT" => ActorType.ServiceAccount,
        _ => throw new InvalidOperationException($"Unknown actor type '{s}'."),
    };

    public static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static AuditEventRecord Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), ParseActor(r.GetString(3)), Str(r, 4),
        Str(r, 5) ?? "", r.GetString(6), r.GetString(7), Str(r, 8), r.GetString(9), Str(r, 10), Str(r, 11),
        Str(r, 12), Str(r, 13), Str(r, 14), r.GetString(15), r.GetInt64(16), Str(r, 17), r.GetString(18));
}

public sealed class AuditVerifier(NpgsqlDataSource dataSource) : IAuditVerifier
{
    public async Task<AuditVerification> VerifyAsync(string? chainId = null, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {AuditRows.Columns} FROM audit_events WHERE (@c::text IS NULL OR chain_id = @c) ORDER BY chain_id, chain_seq", conn);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = (object?)chainId ?? DBNull.Value });

        long checkedCount = 0;
        string? curChain = null; long expectSeq = 0; string? expectPrev = null;
        var seenChains = new HashSet<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var e = AuditRows.Read(r);
                checkedCount++;
                if (e.ChainId != curChain) { curChain = e.ChainId; expectSeq = 1; expectPrev = null; seenChains.Add(curChain); }
                AuditChainBreak? Break(string p) => new(e.ChainId, e.ChainSeq, e.Id, p);
                if (e.ChainSeq != expectSeq) return new(checkedCount, Break($"expected sequence {expectSeq}, found {e.ChainSeq} (missing or reordered event)"));
                if (e.PreviousHash != expectPrev) return new(checkedCount, Break("previous_hash does not match the preceding event"));
                var recomputed = AuditCanonical.Sha256Hex(AuditCanonical.Canonicalize(
                    e.ChainId, e.ChainSeq, e.At, e.Type, WireNames.ToUpperSnake(e.ActorType), e.ActorId, e.ActorName,
                    e.SubjectKind, e.SubjectId, e.RunId, e.RuleVersion, e.Reason, e.Summary, e.RequestId,
                    e.BeforeJson, e.AfterJson, e.PreviousHash));
                if (recomputed != e.Hash) return new(checkedCount, Break("stored hash does not match the event content"));
                expectSeq++; expectPrev = e.Hash;
            }
        }

        // The head must point at the last event of each chain, otherwise trailing events were removed.
        await using var head = new NpgsqlCommand(
            "SELECT h.chain_id, h.last_seq, h.last_hash FROM audit_chain_head h WHERE (@c::text IS NULL OR h.chain_id = @c) ORDER BY h.chain_id", conn);
        head.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = (object?)chainId ?? DBNull.Value });
        var heads = new List<(string Chain, long Seq, string? Hash)>();
        await using (var hr = await head.ExecuteReaderAsync(ct))
            while (await hr.ReadAsync(ct)) heads.Add((hr.GetString(0), hr.GetInt64(1), AuditRows.Str(hr, 2)));
        foreach (var (chain, seq, hash) in heads)
        {
            await using var last = new NpgsqlCommand(
                "SELECT id, chain_seq, hash FROM audit_events WHERE chain_id = @c ORDER BY chain_seq DESC LIMIT 1", conn);
            last.Parameters.AddWithValue("c", chain);
            await using var lr = await last.ExecuteReaderAsync(ct);
            if (!await lr.ReadAsync(ct))
            {
                if (seq != 0) return new(checkedCount, new(chain, seq, 0, "chain head references events that no longer exist"));
                continue;
            }
            if (lr.GetInt64(1) != seq || lr.GetString(2) != hash)
                return new(checkedCount, new(chain, seq, lr.GetInt64(0), "chain head does not match the last event (truncated or rewritten tail)"));
        }
        return new(checkedCount, null);
    }
}

public sealed class AuditReader(NpgsqlDataSource dataSource) : IAuditReader
{
    /// <summary>Newest first, keyset on (occurred_at, id). Cursor is opaque: base64("&lt;utc-ticks&gt;|&lt;id&gt;").</summary>
    public async Task<AuditPage> ListAsync(AuditQuery q, CancellationToken ct = default)
    {
        if (q.Limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(q), "Limit must be 1..200.");
        if (q.Search is { Length: > 200 }) throw new ArgumentException("Search is limited to 200 characters.");
        var where = new List<string>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        void Eq(string col, string p, string? v) { if (v is not null) { where.Add($"{col} = @{p}"); cmd.Parameters.AddWithValue(p, v); } }
        Eq("run_id", "run", q.RunId); Eq("subject_kind", "sk", q.SubjectKind); Eq("subject_id", "sid", q.SubjectId);
        Eq("event_type", "type", q.Type); Eq("actor_id", "aid", q.ActorId);
        if (q.ActorType is { } at) { where.Add("actor_type = @atype"); cmd.Parameters.AddWithValue("atype", WireNames.ToUpperSnake(at)); }
        if (q.From is { } f) { where.Add("occurred_at >= @from"); cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = f }); }
        if (q.To is { } t) { where.Add("occurred_at <= @to"); cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = t }); }
        if (q.RunLegalEntityIds is { } les)
        {
            // Scope: events tied to a run are visible only for runs of the given legal entities; run-less events are global.
            where.Add("(run_id IS NULL OR run_id IN (SELECT id FROM runs WHERE legal_entity_id = ANY(@les)))");
            cmd.Parameters.Add(new NpgsqlParameter("les", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = les.ToArray() });
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var like = "%" + q.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            where.Add("(summary ILIKE @s OR reason ILIKE @s OR subject_id ILIKE @s OR actor_name ILIKE @s OR actor_id ILIKE @s OR event_type ILIKE @s OR request_id ILIKE @s)");
            cmd.Parameters.AddWithValue("s", like);
        }
        if (q.Cursor is not null)
        {
            var (ticks, id) = DecodeCursor(q.Cursor);
            where.Add("(occurred_at, id) < (@cat, @cid)");
            cmd.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.TimestampTz) { Value = new DateTimeOffset(ticks, TimeSpan.Zero) });
            cmd.Parameters.AddWithValue("cid", id);
        }
        cmd.CommandText = $"SELECT {AuditRows.Columns} FROM audit_events"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY occurred_at DESC, id DESC LIMIT @lim";
        cmd.Parameters.AddWithValue("lim", q.Limit + 1);

        var items = new List<AuditEventRecord>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) items.Add(AuditRows.Read(r));
        var more = items.Count > q.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        var next = more ? EncodeCursor(items[^1]) : null;
        return new AuditPage(items, next, more);
    }

    private static string EncodeCursor(AuditEventRecord e) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{e.At.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{e.Id.ToString(CultureInfo.InvariantCulture)}"));

    private static (long Ticks, long Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor), ex);
        }
    }
}
