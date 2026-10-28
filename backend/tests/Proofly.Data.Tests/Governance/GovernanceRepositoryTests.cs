using Proofly.Data.Audit;
using Proofly.Data.Governance;

namespace Proofly.Data.Tests.Governance;

public class GovernanceRepositoryTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    public GovernanceRepositoryTests(SchemaFixture f) => _f = f;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Seeded = [];

    public async Task InitializeAsync()
    {
        // xUnit builds one instance per test on the shared schema: seed once.
        await Gate.WaitAsync();
        try
        {
            if (!Seeded.Add(_f.Schema)) { return; }
            await Seed();
        }
        finally { Gate.Release(); }
    }

    private async Task Seed()
    {
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE1','One','GBP'),('LE2','Two','JPY');
            INSERT INTO users (id, name, email) VALUES ('p','Proposer','p@x.test'),('d','Decider','d@x.test'),('d2','Decider Two','d2@x.test');
            INSERT INTO roles (id, name) VALUES ('APPROVER','Approver') ON CONFLICT DO NOTHING;
            INSERT INTO user_roles (user_id, role_id, legal_entity_id) VALUES ('p','APPROVER','LE1'),('d','APPROVER','LE1'),('d2','APPROVER','LE2');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES
              ('R1','LE1','2026-10-27',1,'v1','REVIEW_REQUIRED'),('R2','LE2','2026-10-27',1,'v1','REVIEW_REQUIRED');
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, policy_version, proposed_at) VALUES
              ('A1','EXCEPTION_RESOLUTION','x','R1',1,'p','APPROVER','1','2026-10-27T10:00:00Z'),
              ('A2','EXCEPTION_RESOLUTION','y','R2',1,'p','APPROVER','1','2026-10-27T10:00:00Z'),
              ('A3','EXCEPTION_RESOLUTION','z','R1',1,'d','APPROVER','1','2026-10-27T10:00:00Z');
            INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('S1','source','src','1','{"mandatory":false}');
            """);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Approvals_same_timestamp_page_by_id_and_exclude_own()
    {
        var repo = new ApprovalRepository(_f.DataSource);
        var p1 = await repo.ListAsync(new ApprovalQuery("p", null, null, false, 1, null));
        Assert.Equal(["A3"], p1.Items.Select(i => i.Id).ToArray());
        Assert.True(p1.HasMore);
        var p2 = await repo.ListAsync(new ApprovalQuery("p", null, null, false, 5, new ApprovalCursor(p1.Items[0].ProposedAt, "A3")));
        Assert.Equal(["A2", "A1"], p2.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Role_must_cover_the_runs_entity_and_scope_filters()
    {
        var repo = new ApprovalRepository(_f.DataSource);
        Assert.Equal(["A1"], (await repo.ListAsync(new ApprovalQuery("d", null, null, true, 10, null))).Items.Select(i => i.Id).ToArray());
        Assert.Equal(["A2"], (await repo.ListAsync(new ApprovalQuery("d2", null, null, true, 10, null))).Items.Select(i => i.Id).ToArray());
        var scoped = await repo.ListAsync(new ApprovalQuery("d", ["LE1"], null, false, 10, null));
        Assert.Equal(["A1", "A3"], scoped.Items.Select(i => i.Id).Order().ToArray());
        Assert.Empty((await repo.ListAsync(new ApprovalQuery("d", [], null, false, 10, null))).Items);
    }

    [Fact]
    public async Task Settings_defaults_to_first_in_scope_entity_and_missing_payload_fields_are_defaults()
    {
        var repo = new SettingsRepository(_f.DataSource);
        Assert.Equal("LE2", (await repo.GetAsync(null, ["LE2"]))!.LegalEntity.Id);
        Assert.Null(await repo.GetAsync("LE2", ["LE1"]));
        var s = (await repo.GetAsync("LE1", null))!;
        Assert.False(s.Sources[0].Mandatory);
        Assert.Equal("", s.FreshnessPolicy);
        Assert.Equal(["GBP"], s.LegalEntity.Currencies.ToArray());
        Assert.Equal(new[] { "d", "p" }, s.Users.Select(u => u.Id).Order().ToArray());
    }

    [Fact]
    public async Task EvidencePack_scope_and_audit_scope()
    {
        var repo = new EvidencePackRepository(_f.DataSource);
        Assert.Null(await repo.GetForRunAsync("R2", ["LE1"]));
        Assert.Equal("NOT_GENERATED", (await repo.GetForRunAsync("R1", null))!.Status);

        var w = new AuditWriter();
        foreach (var run in new[] { "R1", "R2" })
        {
            await using var c = await _f.DataSource.OpenConnectionAsync(); await using var tx = await c.BeginTransactionAsync();
            await w.AppendAsync(tx, new AuditEventInput("RUN_STARTED", AuditActor.System(), "RUN", run, "s", RunId: run));
            await tx.CommitAsync();
        }
        var page = await new AuditReader(_f.DataSource).ListAsync(new AuditQuery(RunLegalEntityIds: ["LE1"]));
        Assert.Equal(["R1"], page.Items.Select(i => i.SubjectId).ToArray());
    }
}
