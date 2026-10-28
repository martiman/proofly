using Npgsql;

namespace Proofly.Data.Tests;

/// <summary>Each test class uses its own schema (search_path) so parallel runs never collide.</summary>
public sealed class SchemaFixture : IAsyncLifetime
{
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using var admin = NpgsqlDataSource.Create(ConnectionTests.TestConnectionString);
        await using (var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"")) { await c.ExecuteNonQueryAsync(); }
        DataSource = NpgsqlDataSource.Create($"{ConnectionTests.TestConnectionString};Search Path={Schema}");
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(ConnectionTests.TestConnectionString);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    public async Task<T?> Scalar<T>(string sql)
    {
        await using var c = DataSource.CreateCommand(sql);
        var v = await c.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    public async Task Exec(string sql)
    {
        await using var c = DataSource.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }
}

public class SchemaTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    public SchemaTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync() => await new MigrationRunner(_f.DataSource).RunAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reapply_is_a_noop()
    {
        var n = EmbeddedMigrations.Load().Count;
        Assert.True(n >= 6);
        Assert.Equal(0, await new MigrationRunner(_f.DataSource).RunAsync());
        Assert.Equal(n, await _f.Scalar<long>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Money_columns_are_numeric_38_6_and_never_float()
    {
        Assert.Equal(0, await _f.Scalar<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND data_type IN ('real','double precision','money')"));
        Assert.Equal(0, await _f.Scalar<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND data_type='numeric' AND column_name ~ '(amount|fees|adjustments|total_)' AND NOT (numeric_precision=38 AND numeric_scale=6)"));
        Assert.True(await _f.Scalar<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND numeric_precision=38 AND numeric_scale=6") > 20);
    }

    [Fact]
    public async Task Non_numeric_money_is_rejected()
    {
        await SeedEntity();
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(
            "INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,due_at,value_amount,value_currency) " +
            "VALUES ('E','R','LE','2026-10-27','t','matching','r','HIGH',now(),'abc','EUR')"));
    }

    [Fact]
    public async Task Currency_check_and_pairing_enforced()
    {
        await SeedEntity();
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(
            "INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,due_at,value_amount,value_currency) " +
            "VALUES ('E','R','LE','2026-10-27','t','matching','r','HIGH',now(),1,'eur')"));
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(
            "INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,due_at,value_amount) " +
            "VALUES ('E','R','LE','2026-10-27','t','matching','r','HIGH',now(),1)"));
    }

    [Fact]
    public async Task Version_is_bumped_on_update()
    {
        await SeedEntity();
        await _f.Exec("UPDATE runs SET state='PROCESSING' WHERE id='R'");
        Assert.Equal(2, await _f.Scalar<int>("SELECT version FROM runs WHERE id='R'"));
    }

    [Fact]
    public async Task Delivery_fingerprint_is_unique_per_source_and_date()
    {
        await SeedEntity();
        const string ins = "INSERT INTO source_deliveries (id,run_id,source_name,business_date,schema_version,file_name,fingerprint_sha256,state) " +
            "VALUES ('{0}','R','transfer_instructions','2026-10-27','1.0','f.csv','{1}','RECEIVED')";
        var fp = new string('a', 64);
        await _f.Exec(string.Format(ins, "D1", fp));
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(string.Format(ins, "D2", fp)));
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(string.Format(ins, "D3", "xyz")));
    }

    [Fact]
    public async Task Audit_events_are_append_only()
    {
        await _f.Exec("INSERT INTO audit_events (event_type,actor_type,subject_kind,subject_id,hash) VALUES ('X','SYSTEM','RUN','R','" + new string('b', 64) + "')");
        var u = await Assert.ThrowsAsync<PostgresException>(() => _f.Exec("UPDATE audit_events SET reason='x'"));
        Assert.Equal("23001", u.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec("DELETE FROM audit_events"));
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec("TRUNCATE audit_events"));
        Assert.Equal(1, await _f.Scalar<long>("SELECT count(*) FROM audit_events"));
    }

    [Fact]
    public async Task Foreign_keys_hold()
    {
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(
            "INSERT INTO runs (id,legal_entity_id,business_date,run_version,ruleset_version,state) VALUES ('R','nope','2026-10-27',1,'v1','PROCESSING')"));
    }

    private async Task SeedEntity()
    {
        await _f.Exec("INSERT INTO legal_entities (id,name,base_currency) VALUES ('LE','x','EUR') ON CONFLICT DO NOTHING");
        await _f.Exec("INSERT INTO runs (id,legal_entity_id,business_date,run_version,ruleset_version,state) VALUES ('R','LE','2026-10-27',1,'v1','AWAITING_EVIDENCE') ON CONFLICT DO NOTHING");
    }
}

public class ContractReconciliationMigrationTests(SchemaFixture shared) : IClassFixture<SchemaFixture>
{
    private const string Id = "0020_contract_reconciliation";

    [Fact]
    public async Task Upgrade_keeps_existing_rows_maps_them_and_is_idempotent()
    {
        await using var f = new SchemaFixture(); // a schema of its own: the old state must not have been migrated already
        await f.InitializeAsync();
        var all = EmbeddedMigrations.Load();
        var runner = new MigrationRunner(f.DataSource);
        await runner.RunAsync(all.Where(s => string.CompareOrdinal(s.Id, Id) < 0));

        // State as written by the previous schema.
        await f.Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE','Entity','GBP');
            INSERT INTO users (id, name, email) VALUES ('u1','Una','u1@x.test'), ('u2','Vic','u2@x.test');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('R','LE','2026-10-27',1,'v1','REVIEW_REQUIRED');
            INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state)
              VALUES ('R','LE','JPY',1,1,0,'COVERED');
            INSERT INTO safeguarding_adjustments (id, run_id, currency, amount, reason, status, proposed_by_user_id, expires_at)
              VALUES ('ADJ','R','GBP',5,'timing','PROPOSED','u1','2026-11-30T23:30:00Z');
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, policy_version, evidence_refs)
              VALUES ('AP','SAFEGUARDING_ADJUSTMENT','ADJ','R',1,'u1','APPROVER','p1','["ev-1","ev-2"]');
            INSERT INTO config_rules (id, kind, name, config_version) VALUES ('RULE','rule','r','1');
            INSERT INTO config_changes (id, config_id, proposed_by_user_id, reason, after, status) VALUES
              ('CC-P','RULE','u1','a','{}','PENDING'), ('CC-I','RULE','u1','b','{}','INVALIDATED'), ('CC-A','RULE','u1','c','{}','APPROVED');
            INSERT INTO match_groups (id, run_id, legal_entity_id, status) VALUES ('MG','R','LE','UNMATCHED');
            INSERT INTO match_candidates (match_group_id, kind, record_table, record_id, amount, currency)
              VALUES ('MG','BANK_TRANSFER','transfer_instructions',1,1,'GBP'), ('MG','LEDGER_ENTRY','ledger_entries',1,1,'GBP');
            INSERT INTO evidence_packs (id, run_id, sha256, storage_ref) VALUES ('EP','R',repeat('a',64),'store/ep');
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
              VALUES ('D','R','LE','transfer_instructions','2026-10-27','1','f.csv',repeat('b',64),'RECEIVED');
            INSERT INTO transfer_instructions (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status)
              VALUES ('D','s1','i1','c1','LE','PAYMENT',1,'GBP','2026-10-27T10:00:00Z','SETTLED');
            """);

        await runner.RunAsync(all);

        Assert.Equal(new DateOnly(2026, 11, 30), await f.Scalar<DateOnly>("SELECT expires_on FROM safeguarding_adjustments WHERE id='ADJ'"));
        Assert.Equal(0, await f.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='safeguarding_adjustments' AND column_name='expires_at'"));
        Assert.Equal("[\"ev-1\", \"ev-2\"]", await f.Scalar<string>("SELECT evidence_refs::text FROM safeguarding_adjustments WHERE id='ADJ'"));
        Assert.Equal("PENDING_APPROVAL", await f.Scalar<string>("SELECT status FROM config_changes WHERE id='CC-P'"));
        Assert.Equal("RETIRED", await f.Scalar<string>("SELECT status FROM config_changes WHERE id='CC-I'"));
        Assert.Equal("APPROVED", await f.Scalar<string>("SELECT status FROM config_changes WHERE id='CC-A'"));
        Assert.Equal("{GBP,JPY}", await f.Scalar<string>("SELECT currencies::text FROM legal_entities WHERE id='LE'"));
        Assert.Equal("LEFT,RIGHT", await f.Scalar<string>("SELECT string_agg(side, ',' ORDER BY id) FROM match_candidates"));
        Assert.Equal("READY", await f.Scalar<string>("SELECT status FROM evidence_packs WHERE id='EP'"));
        Assert.Equal(1, await f.Scalar<long>("SELECT count(*) FROM transfer_instructions WHERE row_number IS NULL")); // kept; the unknown file row stays NULL

        // Running the script again changes nothing and does not fail.
        await f.Exec(all.Single(s => s.Id == Id).Sql);
        Assert.Equal("{GBP,JPY}", await f.Scalar<string>("SELECT currencies::text FROM legal_entities WHERE id='LE'"));
        Assert.Equal("PENDING_APPROVAL", await f.Scalar<string>("SELECT status FROM config_changes WHERE id='CC-P'"));
    }

    [Fact]
    public async Task New_rules_are_enforced()
    {
        await new MigrationRunner(shared.DataSource).RunAsync();
        await shared.Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE2','Entity','EUR');
            INSERT INTO users (id, name, email) VALUES ('u9','Una','u9@x.test');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('R2','LE2','2026-10-27',1,'v1','REVIEW_REQUIRED');
            """);
        // the base currency is always part of the entity's currencies
        Assert.Equal("{EUR}", await shared.Scalar<string>("SELECT currencies::text FROM legal_entities WHERE id='LE2'"));
        // control names are the ControlArea wire names
        await Assert.ThrowsAsync<PostgresException>(() => shared.Exec(
            "INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,due_at) VALUES ('E','R2','LE2','2026-10-27','t','Matching','r','LOW',now())"));
        // an adjustment proposal needs evidence
        await Assert.ThrowsAsync<PostgresException>(() => shared.Exec(
            "INSERT INTO safeguarding_adjustments (id,run_id,currency,amount,reason,proposed_by_user_id) VALUES ('A','R2','EUR',1,'r','u9')"));
        // access changes need a target user and no config row; config changes the opposite
        await shared.Exec("INSERT INTO config_changes (id, change_kind, target_user_id, proposed_by_user_id, reason, after) VALUES ('CC','ACCESS','u9','u9','r','{}')");
        await Assert.ThrowsAsync<PostgresException>(() => shared.Exec(
            "INSERT INTO config_changes (id, change_kind, proposed_by_user_id, reason, after) VALUES ('CC2','ACCESS','u9','r','{}')"));
        await Assert.ThrowsAsync<PostgresException>(() => shared.Exec(
            "INSERT INTO config_changes (id, change_kind, proposed_by_user_id, reason, after, status) VALUES ('CC3','CONFIG','u9','r','{}','PENDING')"));
        // a READY evidence pack must carry its digest and storage
        await Assert.ThrowsAsync<PostgresException>(() => shared.Exec("INSERT INTO evidence_packs (id, run_id) VALUES ('EP','R2')"));
        await shared.Exec("INSERT INTO evidence_packs (id, run_id, status) VALUES ('EP','R2','GENERATING')");
        // group cardinality is 0/1/N:0/1/N
        await Assert.ThrowsAsync<PostgresException>(() => shared.Exec("INSERT INTO match_groups (id, run_id, legal_entity_id, status, cardinality) VALUES ('G','R2','LE2','UNMATCHED','2:2')"));
    }
}
