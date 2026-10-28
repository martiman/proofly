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
            "VALUES ('E','R','LE','2026-10-27','t','c','r','HIGH',now(),'abc','EUR')"));
    }

    [Fact]
    public async Task Currency_check_and_pairing_enforced()
    {
        await SeedEntity();
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(
            "INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,due_at,value_amount,value_currency) " +
            "VALUES ('E','R','LE','2026-10-27','t','c','r','HIGH',now(),1,'eur')"));
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec(
            "INSERT INTO exceptions (id,run_id,legal_entity_id,business_date,title,control,reason_code,severity,due_at,value_amount) " +
            "VALUES ('E','R','LE','2026-10-27','t','c','r','HIGH',now(),1)"));
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
