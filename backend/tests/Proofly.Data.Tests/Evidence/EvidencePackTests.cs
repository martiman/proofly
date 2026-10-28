using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Proofly.Data.Audit;
using Proofly.Data.Evidence;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;

namespace Proofly.Data.Tests.Evidence;

public class EvidencePackTests : IClassFixture<SchemaFixture>, IAsyncLifetime, IDisposable
{
    private readonly SchemaFixture _f;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-evpack-" + Guid.NewGuid().ToString("N"));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Seeded = [];
    private static readonly DateTimeOffset At = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public EvidencePackTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync()
    {
        await Gate.WaitAsync();
        try { if (Seeded.Add(_f.Schema)) { await Seed(); } }
        finally { Gate.Release(); }
    }
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task Seed()
    {
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE1','Entity One','GBP');
            INSERT INTO users (id, name, email) VALUES ('u1','Exporter','e@x.test'),('u2','Preparer','p@x.test'),('u3','Signer','s@x.test');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state) VALUES
              ('R1','LE1','2026-10-27',2,'rules-7','SIGNED_OFF','SIGNED_OFF'),('R2','LE1','2026-10-28',1,'rules-7','REVIEW_REQUIRED','PREPARATION_INCOMPLETE');
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state) VALUES
              ('D1','R1','LE1','bank_a','2026-10-27','1.0','bank_a.csv',repeat('b',64),'RECEIVED'),
              ('D2','R1','LE1','ledger','2026-10-27','1.0','ledger.csv',repeat('c',64),'RECEIVED');
            INSERT INTO delivery_control_totals VALUES ('D1','GBP',100.50,100.50),('D1','JPY',5000,4990),('D2','GBP',100.50,NULL);
            INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking)
              VALUES ('X1','R1','LE1','2026-10-27','Total, "mismatch"','data_completeness','TOTAL_MISMATCH','HIGH','RESOLVED',10.000000,'JPY','2026-10-28T00:00:00Z',true),
                     ('X2','R1','LE1','2026-10-27','Unmatched','matching','UNMATCHED','LOW','OPEN',NULL,NULL,'2026-10-28T00:00:00Z',false);
            INSERT INTO exception_notes (exception_id, author_user_id, body) VALUES ('X1','u1','SECRET-NOTE-BODY');
            INSERT INTO exception_rule_traces (exception_id, rule_id, rule_version, tolerance) VALUES ('X1','DC-1','3','0.01 GBP');
            INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id, status) VALUES ('RP1','X1','RESOLVED','corrected upstream','u2','APPROVED');
            INSERT INTO roles (id, name) VALUES ('APPROVER','Approver') ON CONFLICT DO NOTHING;
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, approver_user_id, required_role, status, decision, rationale, policy_version, decided_at)
              VALUES ('AP1','EXCEPTION_RESOLUTION','RP1','R1',1,'u2','u3','APPROVER','APPROVED','APPROVE','ok','p1',now());
            INSERT INTO match_groups (id, run_id, legal_entity_id, status, difference_amount, currency) VALUES ('MG1','R1','LE1','VALUE_MISMATCH',1.5,'GBP');
            INSERT INTO account_control_results (run_id, account_id, currency, credits, debits, rule_version) VALUES ('R1','ACC-1','GBP',10,4,'3');
            INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state)
              VALUES ('R1','LE1','GBP',100,100,0,'COVERED'),('R1','LE1','JPY',5000,4000,-1000,'SHORTFALL');
            INSERT INTO run_signoffs (run_id, state, prepared_by_user_id, signed_by_user_id, attestation_text, attestation_text_hash, signed_at)
              VALUES ('R1','SIGNED_OFF','u2','u3','I attest',repeat('d',64),'2026-10-27T18:00:00Z');
            """);
        var w = new AuditWriter();
        await using var c = await _f.DataSource.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await w.AppendAsync(tx, new AuditEventInput("RUN_STARTED", AuditActor.System(), "RUN", "R1", "started", RunId: "R1", Reason: "seed"));
        await tx.CommitAsync();
    }

    private EvidencePackBuilder Builder() => new(_f.DataSource, new AuditVerifier(_f.DataSource));
    private static Dictionary<string, string> Entries(byte[] zip)
    {
        using var za = new ZipArchive(new MemoryStream(zip));
        return za.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    [Fact]
    public async Task Same_inputs_yield_same_bytes_and_sha256()
    {
        var req = new EvidencePackRequest("R1", "u1", At);
        var a = await Builder().BuildAsync(req);
        var b = await Builder().BuildAsync(req);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(a.Bytes)), Convert.ToHexString(SHA256.HashData(b.Bytes)));
        var c = await Builder().BuildAsync(req with { GeneratedAt = At.AddMinutes(1) });
        Assert.NotEqual(a.Bytes, c.Bytes);
    }

    [Fact]
    public async Task Pack_has_the_specified_contents_and_manifest_digests_match()
    {
        var build = await Builder().BuildAsync(new EvidencePackRequest("R1", "u1", At));
        var e = Entries(build.Bytes);
        foreach (var f in new[] { "manifest.json", "sources/deliveries.csv", "sources/control_totals.json", "controls/findings.csv", "controls/findings.json",
                     "controls/control_results.json", "controls/match_groups.csv", "controls/account_control_results.csv", "exceptions/resolutions.csv",
                     "exceptions/approvals.csv", "safeguarding/positions.csv", "audit/audit_events.csv", "audit/chain_verification.json", "signoff/attestation.json" })
        {
            Assert.True(e.ContainsKey(f), f);
        }
        using var m = JsonDocument.Parse(e["manifest.json"]);
        var root = m.RootElement;
        Assert.Equal("R1", root.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal("2026-10-27", root.GetProperty("run").GetProperty("businessDate").GetString());
        Assert.Equal("LE1", root.GetProperty("run").GetProperty("legalEntityId").GetString());
        Assert.Equal("rules-7", root.GetProperty("run").GetProperty("rulesetVersion").GetString());
        Assert.Equal("u1", root.GetProperty("generatedBy").GetProperty("userId").GetString());
        Assert.True(root.GetProperty("auditChainValid").GetBoolean());
        foreach (var f in root.GetProperty("files").EnumerateArray())
        {
            var path = f.GetProperty("path").GetString()!;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e[path]))).ToLowerInvariant(), f.GetProperty("sha256").GetString());
        }
        Assert.Contains("D1,bank_a,2026-10-27,1.0,bank_a.csv," + new string('b', 64), e["sources/deliveries.csv"]);
        Assert.Contains("D1,bank_a,JPY,5000.000000,4990.000000,false", e["sources/control_totals.csv"]);
        Assert.Contains("\"Total, \"\"mismatch\"\"\"", e["controls/findings.csv"]);
        Assert.Contains("DC-1", e["controls/findings.csv"]);
        Assert.Contains("RP1", e["exceptions/resolutions.csv"]);
        Assert.Contains("AP1", e["exceptions/approvals.csv"]);
        Assert.Contains("JPY,5000.000000,4000.000000,-1000.000000,SHORTFALL", e["safeguarding/positions.csv"]);
        Assert.Contains("RUN_STARTED", e["audit/audit_events.csv"]);
        Assert.Contains("I attest", e["signoff/attestation.json"]);
        Assert.DoesNotContain(e.Values, v => v.Contains("SECRET-NOTE-BODY"));
    }

    [Fact]
    public async Task Unsigned_run_has_no_attestation()
    {
        var e = Entries((await Builder().BuildAsync(new EvidencePackRequest("R2", null, At))).Bytes);
        Assert.DoesNotContain("signoff/attestation.json", e.Keys);
        Assert.Contains("controls/control_results.json", e.Keys);
    }

    [Fact]
    public async Task Unknown_run_cannot_be_built()
    {
        await Assert.ThrowsAsync<EvidencePackRunNotFoundException>(() => Builder().BuildAsync(new EvidencePackRequest("nope", null, At)));
    }

    private sealed class ThrowingVerifier : IAuditVerifier
    {
        public Task<AuditVerification> VerifyAsync(string? chainId = null, CancellationToken ct = default) => throw new InvalidOperationException("verifier down");
    }

    [Fact]
    public async Task Handler_marks_ready_with_audit_then_is_idempotent_and_failure_is_retriable()
    {
        var store = new EvidenceStore(new EvidenceOptions { Directory = _dir });
        var cmds = new EvidencePackCommands(_f.DataSource, new JobStore(_f.DataSource), new AuditWriter());
        var req = await cmds.RequestAsync("R2", "u1", Guid.NewGuid().ToString());
        Assert.Equal(RequestOutcome.Created, req.Outcome);
        Assert.Equal(RequestOutcome.AlreadyGenerating, (await cmds.RequestAsync("R2", "u1", null)).Outcome);
        Assert.Equal(1, await _f.Scalar<long>("SELECT count(*) FROM jobs WHERE type = 'evidence_pack.generate' AND run_id = 'R2'"));
        var id = req.Pack!.PackId;
        var ctx = new JobContext(1, 1, 5, "R2", null);

        var failing = new EvidencePackJobHandler(_f.DataSource, new EvidencePackBuilder(_f.DataSource, new ThrowingVerifier()), store, new AuditWriter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.HandleAsync(new GenerateEvidencePackPayload(id), ctx, default));
        Assert.Equal("FAILED", await _f.Scalar<string>($"SELECT status FROM evidence_packs WHERE id = '{id}'"));
        Assert.Equal("verifier down", await _f.Scalar<string>($"SELECT failure_reason FROM evidence_packs WHERE id = '{id}'"));

        var handler = new EvidencePackJobHandler(_f.DataSource, Builder(), store, new AuditWriter());
        await handler.HandleAsync(new GenerateEvidencePackPayload(id), ctx, default);
        Assert.Equal("READY", await _f.Scalar<string>($"SELECT status FROM evidence_packs WHERE id = '{id}'"));
        var sha = (await _f.Scalar<string>($"SELECT sha256 FROM evidence_packs WHERE id = '{id}'"))!.Trim();
        Assert.True(await store.VerifyAsync(sha));
        Assert.Equal(new FileInfo(store.PathFor(sha)).Length, await _f.Scalar<long>($"SELECT size_bytes FROM evidence_packs WHERE id = '{id}'"));
        Assert.Null(await _f.Scalar<string>($"SELECT failure_reason FROM evidence_packs WHERE id = '{id}'"));
        var events = await _f.Scalar<long>($"SELECT count(*) FROM audit_events WHERE subject_id = '{id}' AND event_type = 'EVIDENCE_PACK_GENERATED'");
        Assert.Equal(2, events); // requested (actor) + generated (system)

        await handler.HandleAsync(new GenerateEvidencePackPayload(id), ctx, default); // replay: no change
        Assert.Equal(events, await _f.Scalar<long>($"SELECT count(*) FROM audit_events WHERE subject_id = '{id}' AND event_type = 'EVIDENCE_PACK_GENERATED'"));
    }

    [Fact]
    public async Task Request_for_unknown_run_writes_nothing()
    {
        var cmds = new EvidencePackCommands(_f.DataSource, new JobStore(_f.DataSource), new AuditWriter());
        Assert.Equal(RequestOutcome.RunNotFound, (await cmds.RequestAsync("nope", "u1", null)).Outcome);
    }
}
