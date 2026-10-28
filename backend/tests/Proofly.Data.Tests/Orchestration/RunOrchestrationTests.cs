using Npgsql;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Domain.Enums;

namespace Proofly.Data.Tests.Orchestration;

internal sealed class FailAfterAudit(IAuditWriter inner, int failOnCall) : IAuditWriter
{
    private int _calls;
    public Task<AuditEventRecord> AppendAsync(NpgsqlTransaction tx, AuditEventInput input, CancellationToken ct = default) =>
        Interlocked.Increment(ref _calls) >= failOnCall ? throw new InvalidOperationException("audit failed") : inner.AppendAsync(tx, input, ct);
}

/// <summary>Neutral GBP/EUR/JPY evidence inserted as accepted canonical records, evaluated by the real controls.</summary>
public class RunOrchestrationTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private static readonly DateOnly Date = new(2026, 10, 27);
    private static readonly string[] Mandatory = ["transfer_instructions", "internal_ledger_entries", "client_account_balances", "banking_transactions", "safeguarding_balances"];

    private readonly SchemaFixture _f;
    private static int _n;
    public RunOrchestrationTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync() => await new MigrationRunner(_f.DataSource).RunAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IngestionOptions Ingestion() => new() { MandatorySources = Mandatory };
    private RunProcessor Processor(IAuditWriter? audit = null) => new(_f.DataSource, audit ?? new AuditWriter(), Ingestion(), new RunProcessingOptions());
    private RunReprocessService Reprocess() => new(_f.DataSource, new AuditWriter(), new JobStore(_f.DataSource), new RunProcessingOptions());
    private static ProcessRunPayload Payload(string le, string? runId = null) => new(le, Date, runId);
    private static AuditActor Actor => new(ActorType.User, "u-approver", "Approver");

    private static string Fp(string s) => $"encode(sha256('{s}'::bytea),'hex')";

    /// <summary>One neutral legal entity with all five mandatory deliveries. GBP shortfall, JPY covered, EUR surplus, JPY value mismatch.</summary>
    private async Task<string> Seed(bool users = false)
    {
        var le = "LE-" + Interlocked.Increment(ref _n);
        var sources = new (string Source, int Count)[] { ("transfer_instructions", 2), ("internal_ledger_entries", 2), ("client_account_balances", 3), ("banking_transactions", 2), ("safeguarding_balances", 3) };
        var sql = $"INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('{le}','Entity {le}','GBP','{{EUR,JPY}}');\n";
        if (users)
        {
            sql += "INSERT INTO users (id, name, email) VALUES ('u-owner','Owner','owner@x.test'), ('u-signer','Signer','signer@x.test'), ('u-prep','Preparer','prep@x.test') ON CONFLICT DO NOTHING;\n";
        }
        foreach (var (s, n) in sources)
        {
            var id = $"D-{le}-{s}";
            sql += $"""
                INSERT INTO source_deliveries (id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, declared_record_count, received_at, state,
                                               accepted_record_count, rejected_record_count, total_row_count, ingested_at)
                VALUES ('{id}','{le}','{s}','2026-10-27','1.0','{s}.csv',{Fp(id)},{n},'2026-10-28T06:00Z','RECEIVED',{n},0,{n},now());

                """;
        }
        string D(string s) => $"D-{le}-{s}";
        sql += $"""
            INSERT INTO transfer_instructions (delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency,
                instruction_timestamp, requested_value_date, source_status, external_reference) VALUES
              ('{D("transfer_instructions")}',1,'S-1','TRF-1','A1','{le}','BANK_TRANSFER',100,'GBP','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-1'),
              ('{D("transfer_instructions")}',2,'S-2','TRF-2','A2','{le}','BANK_TRANSFER',5000,'JPY','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-2');
            INSERT INTO ledger_entries (delivery_id, row_number, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency,
                transaction_reference, entry_type, source_status) VALUES
              ('{D("internal_ledger_entries")}',1,'L-1','A1','{le}','2026-10-27T10:00Z','2026-10-27','DEBIT',100,'GBP','TRF-1','TRANSFER','POSTED'),
              ('{D("internal_ledger_entries")}',2,'L-2','A2','{le}','2026-10-27T10:00Z','2026-10-27','DEBIT',5000,'JPY','TRF-2','TRANSFER','POSTED');
            INSERT INTO account_balances (delivery_id, row_number, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency, source_timestamp) VALUES
              ('{D("client_account_balances")}',1,'A1','{le}','2026-10-27','DAILY_ROLLFORWARD',1000,900,'GBP','2026-10-27T23:59:59Z'),
              ('{D("client_account_balances")}',2,'A2','{le}','2026-10-27','DAILY_ROLLFORWARD',20000,15000,'JPY','2026-10-27T23:59:59Z'),
              ('{D("client_account_balances")}',3,'A3','{le}','2026-10-27','DAILY_ROLLFORWARD',500,500,'EUR','2026-10-27T23:59:59Z');
            INSERT INTO bank_transactions (delivery_id, row_number, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency,
                debit_credit_indicator, bank_reference, transaction_status, source_timestamp) VALUES
              ('{D("banking_transactions")}',1,'BTX-1','EXT-1','2026-10-27T11:00Z','2026-10-27',100,'GBP','DEBIT','REF-1','BOOKED','2026-10-28T06:00Z'),
              ('{D("banking_transactions")}',2,'BTX-2','EXT-1','2026-10-27T11:00Z','2026-10-27',5200,'JPY','DEBIT','REF-2','BOOKED','2026-10-28T06:00Z');
            INSERT INTO safeguarding_balances (delivery_id, row_number, source_row_id, external_account_id, legal_entity_id, balance_timestamp, balance_type, balance_amount, currency,
                evidence_reference, source_timestamp) VALUES
              ('{D("safeguarding_balances")}',1,'SG-1','SGA-GBP','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',800,'GBP','STMT-1','2026-10-28T06:30Z'),
              ('{D("safeguarding_balances")}',2,'SG-2','SGA-JPY','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',15000,'JPY','STMT-2','2026-10-28T06:30Z'),
              ('{D("safeguarding_balances")}',3,'SG-3','SGA-EUR','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',600,'EUR','STMT-3','2026-10-28T06:30Z');
            """;
        await _f.Exec(sql);
        return le;
    }

    private Task<T?> Q<T>(string sql) => _f.Scalar<T>(sql);
    private static string RunV(string le, int v) => RunProcessor.RunId(le, Date, v);

    [Fact]
    public async Task Process_persists_everything_the_read_api_expects_and_writes_audit()
    {
        var le = await Seed();
        var o = await Processor().ProcessAsync(Payload(le));
        var run = RunV(le, 1);
        Assert.Equal(RunProcessingStatus.Processed, o.Status);
        Assert.Equal(run, o.RunId);
        Assert.Equal("REVIEW_REQUIRED", await Q<string>($"SELECT state FROM runs WHERE id='{run}'")); // the GBP shortfall blocks
        Assert.Equal("PREPARATION_INCOMPLETE", await Q<string>($"SELECT sign_off_state FROM runs WHERE id='{run}'"));
        Assert.Matches("^[0-9a-f]{64}$", (await Q<string>($"SELECT result_hash FROM runs WHERE id='{run}'"))!);
        Assert.Equal("v1+" + new RunProcessingOptions().RulesetVersion, await Q<string>($"SELECT ruleset_version FROM runs WHERE id='{run}'"));
        Assert.Equal(5, await Q<int>($"SELECT jsonb_array_length(input_manifest) FROM runs WHERE id='{run}'"));
        Assert.Equal(5, await Q<long>($"SELECT count(*) FROM source_deliveries WHERE run_id='{run}'"));

        // Safeguarding: one row per run and currency with the documented detail.
        Assert.Equal(3, await Q<long>($"SELECT count(*) FROM safeguarding_positions WHERE run_id='{run}'"));
        Assert.Equal("SHORTFALL", await Q<string>($"SELECT state FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));
        Assert.Equal(-100m, await Q<decimal>($"SELECT difference_amount FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));
        Assert.Equal(900m, await Q<decimal>($"SELECT obligation_amount FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));
        Assert.Equal("COVERED", await Q<string>($"SELECT state FROM safeguarding_positions WHERE run_id='{run}' AND currency='JPY'"));
        Assert.Equal("SURPLUS", await Q<string>($"SELECT state FROM safeguarding_positions WHERE run_id='{run}' AND currency='EUR'"));
        Assert.Equal("SG-1", await Q<string>($"SELECT detail->'balanceLines'->0->>'sourceRowId' FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));
        Assert.Equal("ELIGIBLE", await Q<string>($"SELECT detail->'balanceLines'->0->>'reason' FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));
        Assert.Equal("900.00", await Q<string>($"SELECT detail->'obligationLines'->0->>'amount' FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));
        Assert.Equal("0.00", await Q<string>($"SELECT detail->>'approvedAdjustments' FROM safeguarding_positions WHERE run_id='{run}' AND currency='GBP'"));

        // Independent balances.
        Assert.Equal(3, await Q<long>($"SELECT count(*) FROM account_control_results WHERE run_id='{run}'"));
        Assert.Equal(900m, await Q<decimal>($"SELECT independent FROM account_control_results WHERE run_id='{run}' AND account_id='A1'"));
        Assert.Equal(15000m, await Q<decimal>($"SELECT independent FROM account_control_results WHERE run_id='{run}' AND account_id='A2'"));
        Assert.True(await Q<bool>($"SELECT opening_supported FROM account_control_results WHERE run_id='{run}' AND account_id='A1'"));

        // Exceptions: shortfall (safeguarding, blocking, lower snake control) and the JPY value mismatch with its match group.
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{run}' AND control='safeguarding' AND reason_code='SHORTFALL' AND blocking AND status='OPEN' AND severity='CRITICAL' AND value_currency='GBP' AND value_amount=-100"));
        var mismatch = await Q<string>($"SELECT id FROM exceptions WHERE run_id='{run}' AND control='matching' AND reason_code='VALUE_MISMATCH' AND value_currency='JPY'");
        Assert.NotNull(mismatch);
        Assert.Equal(await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{run}'"), await Q<long>($"SELECT count(*) FROM exception_rule_traces t JOIN exceptions e ON e.id=t.exception_id WHERE e.run_id='{run}'"));
        Assert.Equal("VALUE_MISMATCH", await Q<string>($"SELECT g.status FROM match_groups g JOIN match_group_exceptions m ON m.match_group_id=g.id WHERE m.exception_id='{mismatch}'"));
        Assert.Equal(200m, await Q<decimal>($"SELECT abs(g.difference_amount) FROM match_groups g JOIN match_group_exceptions m ON m.match_group_id=g.id WHERE m.exception_id='{mismatch}'"));
        Assert.Equal("JPY", await Q<string>($"SELECT g.currency FROM match_groups g JOIN match_group_exceptions m ON m.match_group_id=g.id WHERE m.exception_id='{mismatch}'"));
        Assert.NotNull(await Q<string>($"SELECT g.rationale FROM match_groups g JOIN match_group_exceptions m ON m.match_group_id=g.id WHERE m.exception_id='{mismatch}'"));
        Assert.Matches("^(0|1|N):(0|1|N)$", (await Q<string>($"SELECT g.cardinality FROM match_groups g JOIN match_group_exceptions m ON m.match_group_id=g.id WHERE m.exception_id='{mismatch}'"))!);
        Assert.True(await Q<long>($"SELECT count(*) FROM match_candidates c JOIN match_group_exceptions m ON m.match_group_id=c.match_group_id WHERE m.exception_id='{mismatch}'") >= 2);
        Assert.True(await Q<long>($"SELECT count(*) FROM exception_links WHERE exception_id='{mismatch}'") >= 2);

        // Per-control results and audit.
        Assert.Equal(5, await Q<long>($"SELECT count(*) FROM run_control_results WHERE run_id='{run}'"));
        Assert.Contains(await Q<string>($"SELECT state FROM run_control_results WHERE run_id='{run}' AND control='data_completeness'"), new[] { "PASSED", "QUALIFIED" });
        Assert.Equal(5, await Q<long>($"SELECT count(*) FROM audit_events WHERE run_id='{run}' AND event_type='CONTROL_EXECUTED'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM audit_events WHERE run_id='{run}' AND event_type='RUN_STARTED'"));
        Assert.Equal(await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{run}'"), await Q<long>($"SELECT count(*) FROM audit_events WHERE run_id='{run}' AND event_type='EXCEPTION_CREATED'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM audit_events WHERE run_id='{run}' AND event_type='RUN_PROCESSED' AND after->>'event'='RUN_PROCESSED' AND after->>'resultHash'=(SELECT result_hash FROM runs WHERE id='{run}') AND rule_version=(SELECT ruleset_version FROM runs WHERE id='{run}')"));
    }

    [Fact]
    public async Task Same_inputs_and_ruleset_give_a_byte_identical_hash_and_no_duplicates_when_processed_again()
    {
        var a = await Seed();
        var b = await Seed();
        await Processor().ProcessAsync(Payload(a));
        await Processor().ProcessAsync(Payload(b));
        var ha = await Q<string>($"SELECT result_hash FROM runs WHERE id='{RunV(a, 1)}'");
        var hb = await Q<string>($"SELECT result_hash FROM runs WHERE id='{RunV(b, 1)}'");
        Assert.Equal(ha, hb); // different run ids, different database ids, identical evidence

        var before = await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{RunV(a, 1)}'");
        var again = await Processor().ProcessAsync(Payload(a));
        Assert.Equal(RunProcessingStatus.AlreadyProcessed, again.Status);
        Assert.Equal(ha, again.ResultHash);
        Assert.Equal(before, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{RunV(a, 1)}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM (SELECT finding_key FROM exceptions WHERE legal_entity_id='{a}' AND run_id='{RunV(a, 1)}' GROUP BY finding_key HAVING count(*) > 1) x"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM runs WHERE legal_entity_id='{a}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM audit_events WHERE run_id='{RunV(a, 1)}' AND after->>'event'='RUN_PROCESSED'"));
    }

    [Fact]
    public async Task Different_evidence_gives_a_different_hash()
    {
        var a = await Seed();
        var b = await Seed();
        await _f.Exec($"UPDATE safeguarding_balances SET balance_amount = 700 WHERE delivery_id = 'D-{b}-safeguarding_balances' AND source_row_id = 'SG-1'");
        await Processor().ProcessAsync(Payload(a));
        await Processor().ProcessAsync(Payload(b));
        Assert.NotEqual(await Q<string>($"SELECT result_hash FROM runs WHERE id='{RunV(a, 1)}'"), await Q<string>($"SELECT result_hash FROM runs WHERE id='{RunV(b, 1)}'"));
    }

    [Fact]
    public async Task Concurrent_processing_of_one_run_persists_it_once()
    {
        var le = await Seed();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Processor().ProcessAsync(Payload(le))));
        Assert.Equal(1, results.Count(r => r.Status == RunProcessingStatus.Processed));
        Assert.Equal(3, results.Count(r => r.Status == RunProcessingStatus.AlreadyProcessed));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM runs WHERE legal_entity_id='{le}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM (SELECT finding_key FROM exceptions WHERE legal_entity_id='{le}' GROUP BY finding_key HAVING count(*) > 1) x"));
    }

    [Fact]
    public async Task A_failure_while_writing_audit_rolls_back_the_whole_run_version()
    {
        var le = await Seed();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor(new FailAfterAudit(new AuditWriter(), 4)).ProcessAsync(Payload(le)));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM runs WHERE legal_entity_id='{le}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM exceptions WHERE legal_entity_id='{le}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM safeguarding_positions WHERE legal_entity_id='{le}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM audit_events WHERE run_id LIKE 'RUN-{le}-%'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM source_deliveries WHERE legal_entity_id='{le}' AND run_id IS NOT NULL"));
        // The retry then succeeds from scratch.
        Assert.Equal(RunProcessingStatus.Processed, (await Processor().ProcessAsync(Payload(le))).Status);
    }

    [Fact]
    public async Task Reprocess_creates_the_next_version_supersedes_the_old_one_and_continues_exceptions()
    {
        var le = await Seed(users: true);
        var v1 = RunV(le, 1); var v2 = RunV(le, 2);
        await Processor().ProcessAsync(Payload(le));
        var hash1 = await Q<string>($"SELECT result_hash FROM runs WHERE id='{v1}'");
        var mismatch = (await Q<string>($"SELECT id FROM exceptions WHERE run_id='{v1}' AND reason_code='VALUE_MISMATCH'"))!;
        var shortfall = (await Q<string>($"SELECT id FROM exceptions WHERE run_id='{v1}' AND reason_code='SHORTFALL'"))!;
        // Analyst work on version 1: owner, note and an accepted difference on the mismatch; an approved resolution on the shortfall.
        await _f.Exec($"""
            UPDATE exceptions SET owner_user_id='u-owner', status='ACCEPTED_DIFFERENCE', resolution_kind='ACCEPTED_DIFFERENCE', resolution_reason='Bank rounding agreed' WHERE id='{mismatch}';
            INSERT INTO exception_notes (exception_id, author_user_id, body) VALUES ('{mismatch}','u-owner','Checked with the bank');
            INSERT INTO exception_evidence_refs (exception_id, kind, ref) VALUES ('{mismatch}','STATEMENT','STMT-9');
            INSERT INTO exception_timeline (exception_id, kind, actor_type, actor_id, summary) VALUES ('{mismatch}','NOTE','USER','u-owner','Note added');
            UPDATE exceptions SET status='RESOLVED', resolution_kind='RESOLVED', resolution_reason='Top-up scheduled' WHERE id='{shortfall}';
            """);
        var row = await Q<int>($"SELECT version FROM runs WHERE id='{v1}'");

        var wrong = await Reprocess().RequestAsync(new ReprocessRequest(v1, row + 5, "Evidence corrected", Actor));
        Assert.Equal(ReprocessStatus.VersionMismatch, wrong.Status);
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED' AND subject_id='{v1}'"));

        // Evidence changes: the GBP shortfall grows (value changed), the JPY mismatch stays identical.
        await _f.Exec($"UPDATE safeguarding_balances SET balance_amount = 700 WHERE delivery_id = 'D-{le}-safeguarding_balances' AND source_row_id = 'SG-1'; UPDATE source_deliveries SET fingerprint_sha256 = {Fp("changed" + le)} WHERE id = 'D-{le}-safeguarding_balances'");
        var req = await Reprocess().RequestAsync(new ReprocessRequest(v1, row, "Evidence corrected", Actor, "req-1"));
        Assert.Equal(ReprocessStatus.Requested, req.Status);
        Assert.Equal(v2, req.NewRunId);
        Assert.Equal("SUPERSEDED", await Q<string>($"SELECT state FROM runs WHERE id='{v1}'"));
        Assert.Equal(2, await Q<int>($"SELECT run_version FROM runs WHERE id='{v2}'"));
        Assert.Equal(v1, await Q<string>($"SELECT supersedes_run_id FROM runs WHERE id='{v2}'"));
        Assert.Equal("PROCESSING", await Q<string>($"SELECT state FROM runs WHERE id='{v2}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM audit_events WHERE event_type='RUN_REPROCESS_REQUESTED' AND subject_id='{v1}' AND reason='Evidence corrected' AND request_id='req-1'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM jobs WHERE type='run.process' AND dedupe_key='run.process:{v2}' AND run_id='{v2}'"));

        // Only the current version can be reprocessed; a second request on the old one is a conflict.
        Assert.Equal(ReprocessStatus.Superseded, (await Reprocess().RequestAsync(new ReprocessRequest(v1, row + 1, "again", Actor))).Status);
        Assert.Equal(ReprocessStatus.InvalidState, (await Reprocess().RequestAsync(new ReprocessRequest(v2, 1, "again", Actor))).Status);
        Assert.Equal(ReprocessStatus.NotFound, (await Reprocess().RequestAsync(new ReprocessRequest("RUN-none", 1, "again", Actor))).Status);

        var o = await Processor().ProcessAsync(Payload(le, v2));
        Assert.Equal(RunProcessingStatus.Processed, o.Status);
        Assert.Equal(RunProcessingStatus.AlreadyProcessed, (await Processor().ProcessAsync(Payload(le, v2))).Status);
        Assert.NotEqual(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{v2}'"));

        // Version 1 is untouched.
        Assert.Equal("ACCEPTED_DIFFERENCE", await Q<string>($"SELECT status FROM exceptions WHERE id='{mismatch}'"));
        Assert.Equal("RESOLVED", await Q<string>($"SELECT status FROM exceptions WHERE id='{shortfall}'"));
        Assert.Equal(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{v1}'"));

        // Carry-over: same finding, same facts -> identity, owner, notes, evidence, timeline and the accepted resolution continue.
        var m2 = (await Q<string>($"SELECT id FROM exceptions WHERE run_id='{v2}' AND reason_code='VALUE_MISMATCH'"))!;
        Assert.NotEqual(mismatch, m2);
        Assert.Equal(mismatch, await Q<string>($"SELECT predecessor_exception_id FROM exceptions WHERE id='{m2}'"));
        Assert.Equal(await Q<string>($"SELECT finding_key FROM exceptions WHERE id='{mismatch}'"), await Q<string>($"SELECT finding_key FROM exceptions WHERE id='{m2}'"));
        Assert.Equal("ACCEPTED_DIFFERENCE", await Q<string>($"SELECT status FROM exceptions WHERE id='{m2}'"));
        Assert.Equal("Bank rounding agreed", await Q<string>($"SELECT resolution_reason FROM exceptions WHERE id='{m2}'"));
        Assert.Equal("u-owner", await Q<string>($"SELECT owner_user_id FROM exceptions WHERE id='{m2}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exception_notes WHERE exception_id='{m2}' AND body='Checked with the bank'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exception_evidence_refs WHERE exception_id='{m2}' AND ref='STMT-9'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exception_timeline WHERE exception_id='{m2}' AND summary='Note added'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exception_timeline WHERE exception_id='{m2}' AND summary LIKE 'Continues {mismatch}%still applies%'"));

        // The shortfall changed (-100 -> -200): its RESOLVED status does not carry over and it blocks again.
        var s2 = (await Q<string>($"SELECT id FROM exceptions WHERE run_id='{v2}' AND reason_code='SHORTFALL'"))!;
        Assert.Equal(shortfall, await Q<string>($"SELECT predecessor_exception_id FROM exceptions WHERE id='{s2}'"));
        Assert.Equal("OPEN", await Q<string>($"SELECT status FROM exceptions WHERE id='{s2}'"));
        Assert.Null(await Q<string>($"SELECT resolution_kind FROM exceptions WHERE id='{s2}'"));
        Assert.Equal(-200m, await Q<decimal>($"SELECT value_amount FROM exceptions WHERE id='{s2}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exception_timeline WHERE exception_id='{s2}' AND summary LIKE '%no longer applies%'"));
        Assert.Equal("REVIEW_REQUIRED", await Q<string>($"SELECT state FROM runs WHERE id='{v2}'"));

        // Comparison v2 against v1.
        var cmp = await new RunComparisonRepository(_f.DataSource).CompareAsync(v2, v1);
        Assert.Contains(s2, cmp.ChangedExceptionIds);
        Assert.DoesNotContain(m2, cmp.ChangedExceptionIds);
        Assert.DoesNotContain(m2, cmp.NewExceptionIds); // continues an existing exception
        Assert.Empty(cmp.ResolvedExceptionIds);
        Assert.Equal([$"D-{le}-safeguarding_balances"], cmp.ChangedDeliveries);
        var gbp = cmp.SafeguardingDelta.Where(d => d.Change.Currency == "GBP").ToList();
        Assert.Contains(gbp, d => d.Label == "Eligible balances" && d.Change.Amount == -100m);
        Assert.Contains(gbp, d => d.Label == "Safeguarding position" && d.Change.Amount == -100m);
        Assert.DoesNotContain(cmp.SafeguardingDelta, d => d.Change.Currency != "GBP");
        Assert.Equal(5, cmp.Areas.Count);
        Assert.All(cmp.Areas, a => Assert.Equal(0, a.RecordCountDelta));
    }

    [Fact]
    public async Task Compare_reports_new_and_resolved_exceptions_when_a_finding_appears_or_disappears()
    {
        var le = await Seed();
        var v1 = RunV(le, 1); var v2 = RunV(le, 2);
        await Processor().ProcessAsync(Payload(le));
        // Evidence fix: bank now agrees with the instruction (mismatch gone) and the safeguarding balance covers the GBP obligation exactly.
        await _f.Exec($"UPDATE bank_transactions SET amount = 5000 WHERE external_transaction_id='BTX-2' AND delivery_id='D-{le}-banking_transactions'; UPDATE safeguarding_balances SET balance_amount = 900 WHERE source_row_id='SG-1' AND delivery_id='D-{le}-safeguarding_balances'; UPDATE source_deliveries SET fingerprint_sha256 = {Fp("bank-fix" + le)} WHERE id='D-{le}-banking_transactions'");
        var row = await Q<int>($"SELECT version FROM runs WHERE id='{v1}'");
        await Reprocess().RequestAsync(new ReprocessRequest(v1, row, "Corrected bank file", Actor));
        await Processor().ProcessAsync(Payload(le, v2));

        var cmp = await new RunComparisonRepository(_f.DataSource).CompareAsync(v2, v1);
        var v1Mismatch = await Q<string>($"SELECT id FROM exceptions WHERE run_id='{v1}' AND reason_code='VALUE_MISMATCH'");
        var v1Shortfall = await Q<string>($"SELECT id FROM exceptions WHERE run_id='{v1}' AND reason_code='SHORTFALL'");
        Assert.Contains(v1Mismatch!, cmp.ResolvedExceptionIds); // the finding no longer occurs
        Assert.Contains(v1Shortfall!, cmp.ResolvedExceptionIds);
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{v2}' AND reason_code IN ('VALUE_MISMATCH','SHORTFALL')"));
        Assert.All(cmp.NewExceptionIds, id => Assert.Equal(v2, Q<string>($"SELECT run_id FROM exceptions WHERE id='{id}'").Result));
        var reverse = await new RunComparisonRepository(_f.DataSource).CompareAsync(v1, v2);
        Assert.Contains(v1Mismatch!, reverse.NewExceptionIds);
        Assert.Equal("COVERED", await Q<string>($"SELECT state FROM safeguarding_positions WHERE run_id='{v2}' AND currency='GBP'"));
        Assert.Contains(cmp.ChangedDeliveries, d => d.EndsWith("banking_transactions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_signed_off_run_is_never_mutated_and_its_sign_off_is_invalidated_only_when_the_result_changes()
    {
        var le = await Seed(users: true);
        var v1 = RunV(le, 1); var v2 = RunV(le, 2); var v3 = RunV(le, 3);
        await Processor().ProcessAsync(Payload(le));
        var hash1 = await Q<string>($"SELECT result_hash FROM runs WHERE id='{v1}'");
        var exceptions1 = await Q<string>($"SELECT string_agg(id || status, ',' ORDER BY id) FROM exceptions WHERE run_id='{v1}'");
        await _f.Exec($"""
            INSERT INTO run_signoffs (run_id, state, prepared_by_user_id, signed_by_user_id, signed_at) VALUES ('{v1}','SIGNED_OFF','u-prep','u-signer','2026-10-28T10:00Z');
            UPDATE runs SET state='SIGNED_OFF', sign_off_state='SIGNED_OFF' WHERE id='{v1}';
            """);
        var row = await Q<int>($"SELECT version FROM runs WHERE id='{v1}'");

        // Unchanged evidence: the new version has the same hash; the old sign-off is superseded, not invalidated.
        Assert.Equal(ReprocessStatus.Requested, (await Reprocess().RequestAsync(new ReprocessRequest(v1, row, "Periodic re-run", Actor))).Status);
        await Processor().ProcessAsync(Payload(le, v2));
        Assert.Equal(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{v2}'"));
        Assert.Equal("SUPERSEDED", await Q<string>($"SELECT state FROM runs WHERE id='{v1}'"));
        Assert.Equal("SUPERSEDED", await Q<string>($"SELECT sign_off_state FROM runs WHERE id='{v1}'"));
        Assert.Equal("PREPARATION_INCOMPLETE", await Q<string>($"SELECT sign_off_state FROM runs WHERE id='{v2}'")); // starts fresh
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM run_signoffs WHERE run_id='{v2}'"));

        // The signed result, its sign-off record and its exceptions are as signed.
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM run_signoffs WHERE run_id='{v1}' AND state='SIGNED_OFF' AND signed_by_user_id='u-signer'"));
        Assert.Equal(exceptions1, await Q<string>($"SELECT string_agg(id || status, ',' ORDER BY id) FROM exceptions WHERE run_id='{v1}'"));
        Assert.Equal(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{v1}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{v1}' AND predecessor_exception_id IS NOT NULL"));

        // A signed version whose conclusion later changes is invalidated.
        await _f.Exec($"""
            INSERT INTO run_signoffs (run_id, state, prepared_by_user_id, signed_by_user_id, signed_at) VALUES ('{v2}','SIGNED_OFF','u-prep','u-signer','2026-10-28T11:00Z');
            UPDATE runs SET state='SIGNED_OFF', sign_off_state='SIGNED_OFF' WHERE id='{v2}';
            UPDATE safeguarding_balances SET balance_amount = 900 WHERE source_row_id='SG-1' AND delivery_id='D-{le}-safeguarding_balances';
            UPDATE source_deliveries SET fingerprint_sha256 = {Fp("late" + le)} WHERE id = 'D-{le}-safeguarding_balances';
            """);
        row = await Q<int>($"SELECT version FROM runs WHERE id='{v2}'");
        await Reprocess().RequestAsync(new ReprocessRequest(v2, row, "Late corrected balance", Actor));
        await Processor().ProcessAsync(Payload(le, v3));
        Assert.Equal("INVALIDATED", await Q<string>($"SELECT sign_off_state FROM runs WHERE id='{v2}'"));
        Assert.Equal("SUPERSEDED", await Q<string>($"SELECT state FROM runs WHERE id='{v2}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM run_signoffs WHERE run_id='{v2}' AND state='SIGNED_OFF'"));
        Assert.Equal(v2, await Q<string>($"SELECT supersedes_run_id FROM runs WHERE id='{v3}'"));
    }

    [Fact]
    public async Task Runs_that_are_processing_or_awaiting_cannot_be_reprocessed_and_failed_ones_can()
    {
        var le = await Seed();
        await Processor().ProcessAsync(Payload(le));
        var v1 = RunV(le, 1);
        await _f.Exec($"UPDATE runs SET state='FAILED' WHERE id='{v1}'");
        var row = await Q<int>($"SELECT version FROM runs WHERE id='{v1}'");
        Assert.Equal(ReprocessStatus.Requested, (await Reprocess().RequestAsync(new ReprocessRequest(v1, row, "Retry after failure", Actor))).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => Reprocess().RequestAsync(new ReprocessRequest(RunV(le, 2), 1, "no", Actor)));
    }

    [Fact]
    public async Task Evidence_triggers_one_processing_job_once_every_mandatory_source_is_ingested()
    {
        var le = await Seed();
        var enqueuer = new RunEnqueuer(_f.DataSource, new JobStore(_f.DataSource), Ingestion());

        // A missing mandatory source: nothing is enqueued.
        await _f.Exec($"UPDATE source_deliveries SET ingested_at = NULL, state='REJECTED' WHERE id='D-{le}-banking_transactions'");
        Assert.False(await enqueuer.OnDeliveryIngestedAsync($"D-{le}-transfer_instructions"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM jobs WHERE type='run.process' AND dedupe_key LIKE 'run.process:{le}:%'"));

        await _f.Exec($"UPDATE source_deliveries SET ingested_at = now(), state='RECEIVED' WHERE id='D-{le}-banking_transactions'");
        Assert.True(await enqueuer.OnDeliveryIngestedAsync($"D-{le}-banking_transactions"));
        Assert.False(await enqueuer.OnDeliveryIngestedAsync($"D-{le}-transfer_instructions")); // same input set -> same dedupe key
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM jobs WHERE type='run.process' AND dedupe_key LIKE 'run.process:{le}:%'"));

        // A corrected delivery changes the input set: a new job.
        await _f.Exec($"UPDATE source_deliveries SET fingerprint_sha256 = {Fp("corr" + le)} WHERE id='D-{le}-banking_transactions'");
        Assert.True(await enqueuer.OnDeliveryIngestedAsync($"D-{le}-banking_transactions"));
        Assert.Equal(2, await Q<long>($"SELECT count(*) FROM jobs WHERE type='run.process' AND dedupe_key LIKE 'run.process:{le}:%'"));

        // The handler leaves a processed run alone: new evidence needs an explicit reprocess.
        await Processor().ProcessAsync(Payload(le));
        await _f.Exec($"UPDATE safeguarding_balances SET balance_amount = 1 WHERE source_row_id='SG-1' AND delivery_id='D-{le}-safeguarding_balances'");
        var again = await Processor().ProcessAsync(Payload(le));
        Assert.Equal(RunProcessingStatus.AlreadyProcessed, again.Status);
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM runs WHERE legal_entity_id='{le}'"));
    }

    [Fact]
    public async Task A_missing_mandatory_source_is_reported_by_completeness_and_never_passes()
    {
        var le = await Seed();
        await _f.Exec($"DELETE FROM safeguarding_balances WHERE delivery_id='D-{le}-safeguarding_balances'; DELETE FROM source_deliveries WHERE id='D-{le}-safeguarding_balances'");
        await Processor().ProcessAsync(Payload(le));
        var run = RunV(le, 1);
        Assert.Equal("REVIEW_REQUIRED", await Q<string>($"SELECT state FROM runs WHERE id='{run}'"));
        Assert.Equal("FAILED", await Q<string>($"SELECT state FROM run_control_results WHERE run_id='{run}' AND control='data_completeness'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{run}' AND reason_code='SOURCE_MISSING' AND blocking AND control='data_completeness'"));
        // Downstream controls cannot be unqualified passes on an incomplete population.
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM run_control_results WHERE run_id='{run}' AND state='PASSED' AND control <> 'data_completeness'"));
    }
}
