using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Domain.Enums;

namespace Proofly.Data.Tests.Orchestration;

/// <summary>
/// The configuration ruleset in force at run start is recorded on the run and its parameters drive the controls (fix wave): an approved
/// change applies to the next run only. Own database: ruleset_versions is global state.
/// </summary>
public sealed class RulesetOrchestrationTests : IAsyncLifetime
{
    private static readonly DateOnly Date = new(2026, 10, 27);
    private static readonly string[] Mandatory = ["transfer_instructions", "internal_ledger_entries", "client_account_balances", "banking_transactions", "safeguarding_balances"];
    private readonly SchemaFixture _f = new();
    private static int _n;

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await new MigrationRunner(_f.DataSource).RunAsync();
    }

    public Task DisposeAsync() => _f.DisposeAsync();

    private static IngestionOptions Ingestion() => new() { MandatorySources = Mandatory };
    private RunProcessor Processor() => new(_f.DataSource, new AuditWriter(), Ingestion(), new RunProcessingOptions());
    private RunReprocessService Reprocess() => new(_f.DataSource, new AuditWriter(), new JobStore(_f.DataSource), new RunProcessingOptions());
    private static AuditActor Actor => new(ActorType.User, "u-approver", "Approver");
    private Task<T?> Q<T>(string sql) => _f.Scalar<T>(sql);

    private static string Fp(string s) => $"encode(sha256('{s}'::bytea),'hex')";

    /// <summary>One neutral legal entity with all five mandatory deliveries. GBP shortfall, JPY covered, EUR surplus, JPY value mismatch.</summary>
    private async Task<string> Seed()
    {
        var le = "LE-" + Interlocked.Increment(ref _n);
        var sources = new (string Source, int Count)[] { ("transfer_instructions", 2), ("internal_ledger_entries", 2), ("client_account_balances", 3), ("banking_transactions", 2), ("safeguarding_balances", 3) };
        var sql = $"INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('{le}','Entity {le}','GBP','{{EUR,JPY}}');\n";
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


    /// <summary>Activates a ruleset the way ConfigChangeRepository does: config_rules row + a ruleset_versions row with the snapshot.</summary>
    private async Task Activate(string version, int seq, string tolerance, string severity = "MEDIUM")
    {
        var payload = $$"""{"control":"matching","description":"Amount tolerance","tolerance":"{{tolerance}}","severity":"{{severity}}","effectiveFrom":"2026-01-01"}""";
        await _f.Exec($$"""
            INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('MATCH-AMOUNT-TOLERANCE','rule','Matching tolerance','{{version}}','{{payload}}')
              ON CONFLICT (id) DO UPDATE SET payload = EXCLUDED.payload, config_version = EXCLUDED.config_version;
            INSERT INTO ruleset_versions (version, seq, activated_at, effective_from, snapshot)
              SELECT '{{version}}', {{seq}}, now() - interval '1 minute', date '2000-01-01',
                     jsonb_agg(jsonb_build_object('id', id, 'kind', kind, 'name', name, 'version', config_version, 'payload', payload) ORDER BY id) FROM config_rules WHERE enabled;
            """);
    }

    private async Task<string> Reprocessed(string le, int fromVersion)
    {
        var from = RunProcessor.RunId(le, Date, fromVersion);
        var row = await Q<int>($"SELECT version FROM runs WHERE id='{from}'");
        var req = await Reprocess().RequestAsync(new ReprocessRequest(from, row, "Re-run under the activated ruleset", Actor));
        Assert.Equal(ReprocessStatus.Requested, req.Status);
        Assert.Equal(RunProcessingStatus.Processed, (await Processor().ProcessAsync(new ProcessRunPayload(le, Date, req.NewRunId))).Status);
        return req.NewRunId!;
    }

    [Fact]
    public async Task Run_records_the_ruleset_in_force_at_start_and_a_changed_tolerance_changes_only_the_next_run()
    {
        var le = await Seed();
        var codeVersion = new RunProcessingOptions().RulesetVersion;
        await Processor().ProcessAsync(new ProcessRunPayload(le, Date));
        var v1 = RunProcessor.RunId(le, Date, 1);
        var hash1 = await Q<string>($"SELECT result_hash FROM runs WHERE id='{v1}'");
        Assert.Equal($"v1+{codeVersion}", await Q<string>($"SELECT ruleset_version FROM runs WHERE id='{v1}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{v1}' AND control='matching' AND reason_code='VALUE_MISMATCH' AND value_currency='JPY'"));

        // v2: a rule row with the SAME parameters as the code defaults (tolerance 0). New version recorded, identical conclusion and hash.
        await Activate("v2", 2, "0 JPY");
        var run2 = await Reprocessed(le, 1);
        Assert.Equal($"v2+{codeVersion}", await Q<string>($"SELECT ruleset_version FROM runs WHERE id='{run2}'"));
        Assert.Equal(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{run2}'"));

        // v3: the JPY tolerance now covers the 200 difference. The next run's matching outcome and hash change.
        await Activate("v3", 3, "250 JPY");
        var run3 = await Reprocessed(le, 2);
        Assert.Equal($"v3+{codeVersion}", await Q<string>($"SELECT ruleset_version FROM runs WHERE id='{run3}'"));
        Assert.Equal(0, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{run3}' AND control='matching' AND reason_code='VALUE_MISMATCH' AND value_currency='JPY'"));
        Assert.NotEqual(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{run3}'"));

        // Earlier runs keep their recorded version, result hash and findings.
        Assert.Equal($"v1+{codeVersion}", await Q<string>($"SELECT ruleset_version FROM runs WHERE id='{v1}'"));
        Assert.Equal(hash1, await Q<string>($"SELECT result_hash FROM runs WHERE id='{v1}'"));
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM exceptions WHERE run_id='{v1}' AND control='matching' AND reason_code='VALUE_MISMATCH' AND value_currency='JPY'"));
        Assert.Equal($"v2+{codeVersion}", await Q<string>($"SELECT ruleset_version FROM runs WHERE id='{run2}'"));
        // The run's audit event names the version that was applied.
        Assert.Equal(1, await Q<long>($"SELECT count(*) FROM audit_events WHERE event_type='RUN_PROCESSED' AND subject_id='{run3}' AND rule_version='v3+{codeVersion}'"));
    }

    [Fact]
    public void Ruleset_parameters_overlay_the_code_defaults_and_reject_unparseable_values()
    {
        var d = new RunProcessingOptions();
        Assert.Same(d, RulesetParameters.Apply(d, null));
        Assert.Equal(d, RulesetParameters.Apply(d, "[]"));
        var o = RulesetParameters.Apply(d, """
            [{"id":"MATCH-AMOUNT-TOLERANCE","kind":"rule","name":"n","version":"v2","payload":{"control":"matching","tolerance":"0.05 GBP","severity":"HIGH"}},
             {"id":"LEDGER-BALANCE-TOLERANCE","kind":"rule","name":"n","version":"v2","payload":{"tolerance":"1.5 GBP"}},
             {"id":"SAFEGUARDING-STALE","kind":"rule","name":"n","version":"v2","payload":{"severity":"LOW"}},
             {"id":"OTHER","kind":"rule","name":"n","version":"v2","payload":{"tolerance":"9 GBP"}},
             {"id":"freshness_policy","kind":"setting","name":"n","version":"v2","payload":{"text":"x"}}]
            """);
        Assert.Equal(0.05m, o.Matching.ToleranceFor("GBP"));
        Assert.Equal(0m, o.Matching.ToleranceFor("EUR"));
        Assert.Equal(Severity.High, o.Matching.ValueMismatchSeverity);
        Assert.Equal(1.5m, o.Ledger.Tolerance);
        Assert.Equal(Severity.Low, o.Safeguarding.StaleSeverity);
        Assert.Equal(d.Lifecycle, o.Lifecycle);
        Assert.Throws<InvalidOperationException>(() => RulesetParameters.Apply(d,
            """[{"id":"MATCH-AMOUNT-TOLERANCE","kind":"rule","payload":{"tolerance":"-1 GBP"}}]"""));
        Assert.Throws<InvalidOperationException>(() => RulesetParameters.Apply(d,
            """[{"id":"MATCH-UNMATCHED","kind":"rule","payload":{"severity":"URGENT"}}]"""));
    }
}
