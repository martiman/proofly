using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Data.Safeguarding.Commands;

namespace Proofly.Data.Tests.Safeguarding.Commands;

public sealed class ExpiryFixture : IAsyncLifetime
{
    private readonly SchemaFixture _schema = new();
    public Npgsql.NpgsqlDataSource Db => _schema.DataSource;
    public Task<T?> Scalar<T>(string sql) => _schema.Scalar<T>(sql);

    public async Task InitializeAsync()
    {
        await _schema.InitializeAsync();
        await new MigrationRunner(Db).RunAsync();
        await _schema.Exec("""
            INSERT INTO legal_entities (id,name,base_currency) VALUES ('LE-1','One','GBP');
            INSERT INTO users (id,name,email) VALUES ('u-ann','Ann','ann@x.test');
            INSERT INTO runs (id,legal_entity_id,business_date,run_version,ruleset_version,state,sign_off_state) VALUES
             ('R-1','LE-1','2026-10-27',1,'rs-1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE'),
             ('R-SIGNED','LE-1','2026-10-20',1,'rs-1','SIGNED_OFF','SIGNED_OFF');
            INSERT INTO safeguarding_adjustments (id,run_id,currency,amount,reason,status,proposed_by_user_id,evidence_refs,expires_on) VALUES
             ('A-DUE','R-1','GBP',10,'due','APPROVED','u-ann','["e"]','2026-10-27'),
             ('A-TODAY','R-1','GBP',10,'last day','APPROVED','u-ann','["e"]','2026-10-28'),
             ('A-OPEN','R-1','GBP',10,'no expiry','APPROVED','u-ann','["e"]',NULL),
             ('A-PROP','R-1','GBP',10,'proposed','PROPOSED','u-ann','["e"]','2026-10-01'),
             ('A-SIGNED','R-SIGNED','GBP',10,'signed run','APPROVED','u-ann','["e"]','2026-10-21');
            """);
    }

    public Task DisposeAsync() => _schema.DisposeAsync();
}

public sealed class AdjustmentExpiryTests(ExpiryFixture f) : IClassFixture<ExpiryFixture>
{
    [Fact]
    public async Task Expiry_marks_only_due_approved_adjustments_audits_as_system_and_is_idempotent()
    {
        var commands = new AdjustmentCommands(f.Db, new AuditWriter());
        var handler = new ExpireAdjustmentsHandler(commands);
        var ctx = new JobContext(1, 1, 5, null, null);

        await handler.HandleAsync(new ExpireAdjustmentsPayload("2026-10-28"), ctx, default);
        Assert.Equal("EXPIRED", await f.Scalar<string>("SELECT status FROM safeguarding_adjustments WHERE id='A-DUE'"));
        Assert.Equal("APPROVED", await f.Scalar<string>("SELECT status FROM safeguarding_adjustments WHERE id='A-TODAY'")); // expires_on == today still counts
        Assert.Equal("APPROVED", await f.Scalar<string>("SELECT status FROM safeguarding_adjustments WHERE id='A-OPEN'"));
        Assert.Equal("PROPOSED", await f.Scalar<string>("SELECT status FROM safeguarding_adjustments WHERE id='A-PROP'"));
        Assert.Equal("APPROVED", await f.Scalar<string>("SELECT status FROM safeguarding_adjustments WHERE id='A-SIGNED'")); // signed runs are never mutated
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM audit_events WHERE subject_id='A-DUE' AND actor_type='SYSTEM' AND event_type='ADJUSTMENT_DECIDED' AND chain_id='LE-1'"));

        await handler.HandleAsync(new ExpireAdjustmentsPayload("2026-10-28"), ctx, default);
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM audit_events WHERE subject_id='A-DUE'"));

        await handler.HandleAsync(new ExpireAdjustmentsPayload("2026-10-29"), ctx, default);
        Assert.Equal("EXPIRED", await f.Scalar<string>("SELECT status FROM safeguarding_adjustments WHERE id='A-TODAY'"));
    }

    [Fact]
    public async Task Invalid_date_is_a_permanent_failure()
    {
        var handler = new ExpireAdjustmentsHandler(new AdjustmentCommands(f.Db, new AuditWriter()));
        await Assert.ThrowsAsync<PermanentJobException>(() => handler.HandleAsync(new ExpireAdjustmentsPayload("28/10/2026"), new JobContext(1, 1, 5, null, null), default));
    }
}
