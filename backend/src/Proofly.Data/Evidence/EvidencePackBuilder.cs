using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Proofly.Data.Audit;

namespace Proofly.Data.Evidence;

/// <summary>Who generated the pack and when. Both are inputs so the same inputs always yield the same bytes.</summary>
public sealed record EvidencePackRequest(string RunId, string? GeneratedByUserId, DateTimeOffset GeneratedAt);

public sealed record EvidencePackFile(string Path, string Sha256, long SizeBytes);

public sealed record EvidencePackBuild(byte[] Bytes, IReadOnlyList<EvidencePackFile> Files, string ResultHash);

/// <summary>Thrown when the run does not exist; the pack cannot be built.</summary>
public sealed class EvidencePackRunNotFoundException(string runId) : Exception($"Run '{runId}' does not exist.");

/// <summary>
/// Builds the evidence pack for a run as a deterministic ZIP: entries sorted by path, fixed entry timestamps, every row
/// set ordered by a total key, money as decimal strings, LF line endings, UTF-8 without BOM. Reads one consistent
/// snapshot (REPEATABLE READ, read-only). Contains only what the pack spec lists: no raw source rows, no secrets.
/// </summary>
public sealed class EvidencePackBuilder(NpgsqlDataSource dataSource, IAuditVerifier verifier)
{
    public const string SchemaVersion = "1";
    private static readonly DateTimeOffset ZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8 = new(false);

    private sealed record Table(string[] Columns, List<object?[]> Rows);

    public async Task<EvidencePackBuild> BuildAsync(EvidencePackRequest req, CancellationToken ct = default)
    {
        var verification = await verifier.VerifyAsync(null, ct);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);

        // Run header.
        var runTable = await Query(conn, tx, """
            SELECT r.id, r.run_version, r.business_date, r.legal_entity_id, le.name, r.ruleset_version, r.state, r.sign_off_state
            FROM runs r JOIN legal_entities le ON le.id = r.legal_entity_id WHERE r.id = @r
            """, req.RunId, ct);
        if (runTable.Rows.Count == 0) { throw new EvidencePackRunNotFoundException(req.RunId); }
        var run = runTable.Rows[0];

        string? generatorName = null;
        if (req.GeneratedByUserId is not null)
        {
            var u = await Query(conn, tx, "SELECT name FROM users WHERE id = @r", req.GeneratedByUserId, ct);
            generatorName = u.Rows.Count == 0 ? null : (string?)u.Rows[0][0];
        }

        async Task Add(string path, string sql)
        {
            var t = await Query(conn, tx, sql, req.RunId, ct);
            files[path + ".csv"] = Utf8.GetBytes(Csv(t));
            files[path + ".json"] = Json(w => WriteRows(w, t));
        }

        await Add("sources/deliveries", """
            SELECT id AS delivery_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state,
                   declared_record_count, accepted_record_count, rejected_record_count, expected_by, received_at, supersedes_delivery_id
            FROM source_deliveries WHERE run_id = @r ORDER BY source_name, id
            """);
        await Add("sources/control_totals", """
            SELECT t.delivery_id, d.source_name, t.currency, t.declared_amount, t.actual_amount,
                   CASE WHEN t.declared_amount IS NULL OR t.actual_amount IS NULL THEN NULL
                        ELSE (t.declared_amount = t.actual_amount) END AS totals_agree
            FROM delivery_control_totals t JOIN source_deliveries d ON d.id = t.delivery_id
            WHERE d.run_id = @r ORDER BY d.source_name, t.delivery_id, t.currency
            """);
        await Add("controls/control_results", """
            SELECT c.control,
                   count(e.id) AS findings,
                   count(e.id) FILTER (WHERE e.blocking) AS blocking_findings,
                   count(e.id) FILTER (WHERE e.status IN ('OPEN','IN_INVESTIGATION','WAITING_FOR_DATA','PENDING_APPROVAL')) AS unresolved_findings,
                   count(e.id) FILTER (WHERE e.severity = 'CRITICAL') AS critical,
                   count(e.id) FILTER (WHERE e.severity = 'HIGH') AS high,
                   count(e.id) FILTER (WHERE e.severity = 'MEDIUM') AS medium,
                   count(e.id) FILTER (WHERE e.severity = 'LOW') AS low
            FROM (VALUES ('data_completeness'),('independent_ledger'),('matching'),('safeguarding'),('transaction_lifecycle')) AS c(control)
            LEFT JOIN exceptions e ON e.control = c.control AND e.run_id = @r
            GROUP BY c.control ORDER BY c.control
            """);
        await Add("controls/findings", """
            SELECT e.id AS finding_id, e.control, e.reason_code, e.severity, e.status, e.blocking, e.title,
                   e.value_amount, e.value_currency, e.owner_user_id, e.due_at, e.delivery_id, e.source_file, e.source_row_id,
                   e.resolution_kind, e.resolution_reason, t.rule_id, t.rule_version, t.tolerance, e.created_at
            FROM exceptions e LEFT JOIN exception_rule_traces t ON t.exception_id = e.id
            WHERE e.run_id = @r ORDER BY e.control, e.id
            """);
        await Add("controls/match_groups", """
            SELECT id AS match_group_id, status, difference_amount, currency, manual, rule_id, tolerance, rationale, cardinality
            FROM match_groups WHERE run_id = @r ORDER BY id
            """);
        await Add("controls/account_control_results", """
            SELECT account_id, currency, opening, credits, debits, adjustments, independent, recorded, difference, opening_supported,
                   jsonb_array_length(contributions) AS contribution_count, jsonb_array_length(exclusions) AS exclusion_count, rule_version
            FROM account_control_results WHERE run_id = @r ORDER BY account_id, currency
            """);
        await Add("exceptions/resolutions", """
            SELECT p.id AS proposal_id, p.exception_id, p.kind, p.rationale, p.proposed_by_user_id, p.status, p.created_at
            FROM resolution_proposals p JOIN exceptions e ON e.id = p.exception_id
            WHERE e.run_id = @r ORDER BY p.exception_id, p.id
            """);
        await Add("exceptions/approvals", """
            SELECT id AS approval_id, object_type, object_id, proposal_version, proposer_user_id, approver_user_id, required_role,
                   status, decision, rationale, policy_version, proposed_at, decided_at
            FROM approvals WHERE run_id = @r ORDER BY object_type, object_id, id
            """);
        await Add("safeguarding/positions", """
            SELECT currency, obligation_amount, eligible_amount, difference_amount, state
            FROM safeguarding_positions WHERE run_id = @r ORDER BY currency
            """);
        await Add("safeguarding/adjustments", """
            SELECT id AS adjustment_id, currency, amount, reason, status, proposed_by_user_id, expires_on, evidence_refs::text AS evidence_refs
            FROM safeguarding_adjustments WHERE run_id = @r ORDER BY id
            """);
        await Add("audit/audit_events", """
            SELECT id AS event_id, occurred_at, event_type, actor_type, actor_id, actor_name, subject_kind, subject_id, summary, reason,
                   rule_version, request_id, chain_id, chain_seq, previous_hash, hash
            FROM audit_events WHERE run_id = @r ORDER BY id
            """);

        files["audit/chain_verification.json"] = Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("scope", "all audit chains");
            w.WriteNumber("eventsChecked", verification.EventsChecked);
            w.WriteBoolean("valid", verification.IsValid);
            w.WritePropertyName("firstBreak");
            if (verification.FirstBreak is { } b)
            {
                w.WriteStartObject();
                w.WriteString("chainId", b.ChainId); w.WriteNumber("chainSeq", b.ChainSeq); w.WriteNumber("eventId", b.EventId); w.WriteString("problem", b.Problem);
                w.WriteEndObject();
            }
            else { w.WriteNullValue(); }
            w.WriteEndObject();
        });

        var signoff = await Query(conn, tx, """
            SELECT s.state, s.prepared_by_user_id, s.signed_by_user_id, u.name AS signed_by_name, s.signed_at, s.attestation_text,
                   s.attestation_text_hash, s.approval_id
            FROM run_signoffs s LEFT JOIN users u ON u.id = s.signed_by_user_id
            WHERE s.run_id = @r AND s.state = 'SIGNED_OFF' ORDER BY s.id DESC LIMIT 1
            """, req.RunId, ct);
        if (signoff.Rows.Count > 0)
        {
            files["signoff/attestation.json"] = Json(w => WriteRows(w, signoff, single: true));
        }
        await tx.CommitAsync(ct);

        // Manifest: lists every other file with its digest; resultHash binds the whole content set.
        var listed = files.Select(f => new EvidencePackFile(f.Key, Sha(f.Value), f.Value.Length)).ToList();
        var resultHash = Sha(Utf8.GetBytes(string.Concat(listed.Select(f => $"{f.Path}\t{f.Sha256}\n"))));
        files["manifest.json"] = Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", SchemaVersion);
            w.WritePropertyName("run");
            w.WriteStartObject();
            w.WriteString("id", (string)run[0]!);
            w.WriteString("version", Convert.ToString(run[1], CultureInfo.InvariantCulture));
            w.WriteString("businessDate", (string)run[2]!);
            w.WriteString("legalEntityId", (string)run[3]!);
            w.WriteString("legalEntityName", (string)run[4]!);
            w.WriteString("rulesetVersion", (string)run[5]!);
            w.WriteString("state", (string)run[6]!);
            w.WriteString("signOffState", (string)run[7]!);
            w.WriteEndObject();
            w.WriteString("resultHash", resultHash);
            w.WritePropertyName("generatedBy");
            w.WriteStartObject();
            w.WriteString("userId", req.GeneratedByUserId);
            w.WriteString("name", generatorName);
            w.WriteEndObject();
            w.WriteString("generatedAt", Ts(req.GeneratedAt));
            w.WriteBoolean("signedOff", signoff.Rows.Count > 0);
            w.WriteBoolean("auditChainValid", verification.IsValid);
            w.WritePropertyName("files");
            w.WriteStartArray();
            foreach (var f in listed)
            {
                w.WriteStartObject(); w.WriteString("path", f.Path); w.WriteString("sha256", f.Sha256); w.WriteNumber("sizeBytes", f.SizeBytes); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        var all = files.Select(f => new EvidencePackFile(f.Key, Sha(f.Value), f.Value.Length)).ToList();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTime;
                entry.ExternalAttributes = 0x81A4 << 16; // regular file, 0644
                await using var s = entry.Open();
                await s.WriteAsync(bytes, ct);
            }
        }
        return new EvidencePackBuild(ms.ToArray(), all, resultHash);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Ts(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Table> Query(NpgsqlConnection c, NpgsqlTransaction tx, string sql, string runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = runId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        var rows = new List<object?[]>();
        while (await r.ReadAsync(ct))
        {
            var row = new object?[cols.Length];
            for (var i = 0; i < cols.Length; i++) { row[i] = r.IsDBNull(i) ? null : Normalize(r.GetValue(i)); }
            rows.Add(row);
        }
        return new Table(cols, rows);
    }

    /// <summary>Strings, bools and integers stay typed; decimals, dates and timestamps become invariant strings.</summary>
    private static object Normalize(object v) => v switch
    {
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime { Kind: DateTimeKind.Unspecified } dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => Ts(new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))),
        DateTimeOffset dto => Ts(dto),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        int or long or short or bool or string => v,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(w);
        }
        return ms.ToArray();
    }

    private static void WriteRows(Utf8JsonWriter w, Table t, bool single = false)
    {
        if (!single) { w.WriteStartArray(); }
        foreach (var row in t.Rows)
        {
            w.WriteStartObject();
            for (var i = 0; i < t.Columns.Length; i++)
            {
                w.WritePropertyName(t.Columns[i]);
                switch (row[i])
                {
                    case null: w.WriteNullValue(); break;
                    case bool b: w.WriteBooleanValue(b); break;
                    case int n: w.WriteNumberValue(n); break;
                    case long n: w.WriteNumberValue(n); break;
                    case short n: w.WriteNumberValue(n); break;
                    default: w.WriteStringValue(Convert.ToString(row[i], CultureInfo.InvariantCulture)); break;
                }
            }
            w.WriteEndObject();
            if (single) { return; }
        }
        if (!single) { w.WriteEndArray(); }
    }

    private static string Csv(Table t)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', t.Columns)).Append('\n');
        foreach (var row in t.Rows)
        {
            sb.Append(string.Join(',', row.Select(v => Cell(v)))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Cell(object? v)
    {
        var s = v switch { null => "", bool b => b ? "true" : "false", _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
        return s.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
