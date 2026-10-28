using Proofly.Data.Runs;

namespace Proofly.Data.Tests.Runs;

public class RunReadRepositoryTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private RunReadRepository Repo => new(_f.DataSource, Clock);
    public RunReadRepositoryTests(SchemaFixture f) => _f = f;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    public async Task InitializeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await new MigrationRunner(_f.DataSource).RunAsync();
            if (await _f.Scalar<long>("SELECT count(*) FROM runs") == 0) { await _f.Exec(Seed); }
        }
        finally { Gate.Release(); }
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Seed = """
        INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-1','Alpha Ltd','GBP'), ('LE-2','Beta GmbH','EUR'), ('LE-3','Empty KK','JPY');
        INSERT INTO users (id, name, email) VALUES ('u1','Una','u1@x.test'), ('u2','Vic','u2@x.test');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, started_at, last_evidence_arrival_at) VALUES
          ('R-1','LE-1','2026-10-26',1,'rs1','SUPERSEDED','SUPERSEDED','2026-10-26T08:00Z','2026-10-26T07:00Z');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, supersedes_run_id, started_at) VALUES
          ('R-2','LE-1','2026-10-26',2,'rs1','SIGNED_OFF','SIGNED_OFF','R-1','2026-10-26T12:00Z');
        INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, started_at, last_evidence_arrival_at) VALUES
          ('R-3','LE-1','2026-10-27',1,'rs1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE','2026-10-27T08:00Z','2026-10-27T07:30Z'),
          ('R-4','LE-2','2026-10-27',1,'rs1','PROCESSING','PREPARATION_INCOMPLETE','2026-10-27T08:00Z','2026-10-27T07:45Z');
        INSERT INTO run_signoffs (run_id, state, prepared_by_user_id, signed_by_user_id, signed_at) VALUES ('R-2','SIGNED_OFF','u1','u2','2026-10-26T15:00Z');
        INSERT INTO run_signoffs (run_id, state, prepared_by_user_id) VALUES ('R-3','PREPARATION_INCOMPLETE','u1');

        INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, declared_record_count, expected_by, received_at, state, accepted_record_count, rejected_record_count, total_row_count) VALUES
          ('D-1','R-3','LE-1','ledger','2026-10-27','1.0','ledger.csv',repeat('a',64),12,'2026-10-27T06:00Z','2026-10-27T05:59Z','RECEIVED',10,2,12),
          ('D-2','R-3','LE-1','bank','2026-10-27','1.0','bank.csv',repeat('b',64),5,'2026-10-27T06:00Z',NULL,'LATE',0,0,NULL);
        INSERT INTO delivery_control_totals (delivery_id, currency, declared_amount, actual_amount) VALUES
          ('D-1','GBP',100.5,100.5), ('D-1','EUR',50,NULL), ('D-1','JPY',NULL,7000);
        INSERT INTO ledger_entries (delivery_id, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency, entry_type, source_status) VALUES
          ('D-1','L1','A1','LE-1','2026-10-27T01:00Z','2026-10-27','CREDIT',10,'GBP','POSTING','BOOKED'),
          ('D-1','L2','A1','LE-1','2026-10-27T01:00Z','2026-10-27','DEBIT',4,'GBP','POSTING','BOOKED'),
          ('D-1','L3','A2','LE-1','2026-10-27T01:00Z','2026-10-27','CREDIT',900,'JPY','POSTING','BOOKED');

        INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state, detail) VALUES
          ('R-3','LE-1','GBP',1000,900,-80,'SHORTFALL','{"qualifications":["stale balance"]}'),
          ('R-3','LE-1','JPY',50000,50000,0,'COVERED','{}'),
          ('R-2','LE-1','GBP',1000,950,-50,'SHORTFALL','{}');
        INSERT INTO safeguarding_adjustments (id, run_id, currency, amount, reason, status, proposed_by_user_id, expires_at) VALUES
          ('ADJ-1','R-3','GBP',20,'timing','APPROVED','u1',NULL),
          ('ADJ-2','R-3','GBP',5,'expired','APPROVED','u1','2026-10-01T00:00Z'),
          ('ADJ-3','R-3','GBP',99,'unapproved','PROPOSED','u1',NULL);

        INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, owner_user_id, due_at, blocking, source_file, source_row_id, delivery_id, created_at) VALUES
          ('E-1','R-3','LE-1','2026-10-27','Shortfall','safeguarding','SHORTFALL','HIGH','OPEN',80,'GBP','u1','2026-10-28T00:00Z',true,NULL,NULL,NULL,'2026-10-26T10:00Z'),
          ('E-2','R-3','LE-1','2026-10-27','Unmatched','matching','UNMATCHED','MEDIUM','OPEN',5,'EUR',NULL,'2026-10-29T00:00Z',false,'bank.csv','7','D-2','2026-10-27T10:00Z'),
          ('E-3','R-3','LE-1','2026-10-27','Done','matching','UNMATCHED','CRITICAL','RESOLVED',9,'EUR',NULL,'2026-10-29T00:00Z',false,NULL,NULL,NULL,'2026-10-27T10:00Z'),
          ('E-4','R-3','LE-1','2026-10-27','Unmatched2','matching','UNMATCHED','MEDIUM','IN_INVESTIGATION',7,'EUR',NULL,'2026-10-29T00:00Z',false,NULL,NULL,NULL,'2026-10-27T10:00Z'),
          ('E-5','R-2','LE-1','2026-10-26','Old','matching','UNMATCHED','LOW','RESOLVED',NULL,NULL,NULL,'2026-10-29T00:00Z',false,NULL,NULL,NULL,'2026-10-26T10:00Z');
        INSERT INTO match_groups (id, run_id, legal_entity_id, status) VALUES ('MG-1','R-3','LE-1','UNMATCHED'), ('MG-2','R-3','LE-1','MATCHED'), ('MG-3','R-2','LE-1','MATCHED');
        """;

    [Fact]
    public async Task Legal_entities_are_scoped_and_carry_currencies()
    {
        var all = await Repo.ListLegalEntitiesAsync(EntityScope.Everything);
        Assert.Equal(["LE-1", "LE-2", "LE-3"], all.Select(e => e.Id));
        Assert.Equal(["GBP", "JPY"], all[0].Currencies);
        Assert.Equal(["EUR"], all[1].Currencies);
        Assert.Equal(["LE-2"], (await Repo.ListLegalEntitiesAsync(EntityScope.Only("LE-2"))).Select(e => e.Id));
        Assert.Empty(await Repo.ListLegalEntitiesAsync(EntityScope.Only()));
    }

    [Fact]
    public async Task Runs_list_sorts_and_hides_superseded_by_default()
    {
        var page = await Repo.ListRunsAsync(EntityScope.Everything, new RunListFilter(null, null, false), 10, null, true);
        Assert.Equal(["R-3", "R-4", "R-2"], page.Items.Select(r => r.Id));
        Assert.Equal(3, page.Total);
        var withSup = await Repo.ListRunsAsync(EntityScope.Everything, new RunListFilter(null, null, true), 10, null, false);
        Assert.Equal(["R-3", "R-4", "R-2", "R-1"], withSup.Items.Select(r => r.Id));
        Assert.Null(withSup.Total);
    }

    [Fact]
    public async Task Runs_list_filters_and_scope()
    {
        var f = new RunListFilter("LE-1", new DateOnly(2026, 10, 26), true);
        Assert.Equal(["R-2", "R-1"], (await Repo.ListRunsAsync(EntityScope.Everything, f, 10, null, false)).Items.Select(r => r.Id));
        Assert.Equal(["R-4"], (await Repo.ListRunsAsync(EntityScope.Only("LE-2"), new RunListFilter(null, null, true), 10, null, false)).Items.Select(r => r.Id));
        Assert.Empty((await Repo.ListRunsAsync(EntityScope.Only("LE-2"), new RunListFilter("LE-1", null, true), 10, null, false)).Items);
        Assert.Empty((await Repo.ListRunsAsync(EntityScope.Everything, new RunListFilter("LE-3", null, true), 10, null, true)).Items);
    }

    [Fact]
    public async Task Runs_list_pages_with_a_keyset_cursor()
    {
        var f = new RunListFilter(null, null, true);
        var seen = new List<string>();
        RunCursor? cursor = null;
        for (var i = 0; i < 4; i++)
        {
            var rows = (await Repo.ListRunsAsync(EntityScope.Everything, f, 1, cursor, false)).Items;
            Assert.Equal(i == 3 ? 1 : 2, rows.Count);
            var last = rows[0];
            seen.Add(last.Id);
            cursor = new RunCursor(last.BusinessDate, last.LegalEntityId, last.Version, last.Id);
        }
        Assert.Equal(["R-3", "R-4", "R-2", "R-1"], seen);
    }

    [Fact]
    public async Task Run_carries_chain_users_and_etag_version()
    {
        var r1 = (await Repo.GetRunAsync(EntityScope.Everything, "R-1"))!;
        Assert.Equal("R-2", r1.SupersededByRunId);
        Assert.Equal(r1.StartedAt, r1.LastEvidenceArrivalAt - TimeSpan.FromHours(-1)); // 07:00 evidence, 08:00 start
        var r2 = (await Repo.GetRunAsync(EntityScope.Everything, "R-2"))!;
        Assert.Equal("R-1", r2.SupersedesRunId);
        Assert.Equal("Una", r2.Preparer!.Name);
        Assert.Equal("u2", r2.SignedOffBy!.Id);
        Assert.NotNull(r2.SignedOffAt);
        Assert.Equal(r2.StartedAt, r2.LastEvidenceArrivalAt); // no evidence timestamp recorded: falls back to start
        var r3 = (await Repo.GetRunAsync(EntityScope.Everything, "R-3"))!;
        Assert.Null(r3.SignedOffBy);
        Assert.Equal(1, r3.RowVersion);
        await _f.Exec("UPDATE runs SET state = 'REVIEW_REQUIRED' WHERE id = 'R-4'");
        Assert.Equal(2, (await Repo.GetRunAsync(EntityScope.Everything, "R-4"))!.RowVersion);
    }

    [Fact]
    public async Task Run_outside_scope_or_missing_is_null()
    {
        Assert.Null(await Repo.GetRunAsync(EntityScope.Only("LE-2"), "R-3"));
        Assert.Null(await Repo.GetRunAsync(EntityScope.Everything, "nope"));
        Assert.Null(await Repo.GetControlRoomAsync(EntityScope.Only("LE-2"), "R-3"));
    }

    [Fact]
    public async Task Control_room_aggregates_are_derived_per_currency_from_the_tables()
    {
        var room = (await Repo.GetControlRoomAsync(EntityScope.Everything, "R-3"))!;

        Assert.Equal(["D-1", "D-2"], room.EvidenceReadiness.Select(d => d.Id).Order());
        var d1 = room.EvidenceReadiness.Single(d => d.Id == "D-1");
        Assert.Equal([new MoneyRow(50m, "EUR"), new MoneyRow(100.5m, "GBP")], d1.DeclaredTotals); // JPY only measured, not declared
        Assert.Equal((12, 12), (d1.DeclaredRecordCount, d1.ActualRecordCount));
        var d2 = room.EvidenceReadiness.Single(d => d.Id == "D-2");
        Assert.Equal(["E-2"], d2.FindingIds);
        Assert.Null(d2.ReceivedAt);

        Assert.Equal(["data_completeness", "matching", "independent_ledger", "safeguarding"], room.ControlAreas.Select(a => a.Area));
        var completeness = room.ControlAreas[0];
        Assert.Equal(("PASSED", 12, 0), (completeness.State, completeness.RecordCount, completeness.OpenExceptions));
        var matching = room.ControlAreas[1];
        Assert.Equal(("REVIEW_REQUIRED", 2, 2, 1, 1), (matching.State, matching.RecordCount, matching.OpenExceptions, matching.PreviousRecordCount, matching.ChangeFromPrevious));
        Assert.Equal([new MoneyRow(12m, "EUR")], matching.Value); // open only: E-2 5 + E-4 7; resolved E-3 excluded
        var ledger = room.ControlAreas[2];
        Assert.Equal((3, 0, 3, "PASSED"), (ledger.RecordCount, ledger.PreviousRecordCount, ledger.ChangeFromPrevious, ledger.State));
        var sg = room.ControlAreas[3];
        Assert.Equal(("BLOCKED", 2, 1, 1), (sg.State, sg.RecordCount, sg.PreviousRecordCount, sg.ChangeFromPrevious));
        Assert.Equal([new MoneyRow(80m, "GBP")], sg.Value);
        Assert.DoesNotContain(room.ControlAreas, a => a.Area == "transaction_lifecycle");

        Assert.Equal(["E-1", "E-2", "E-4"], room.AttentionQueue.Select(e => e.Id));
        var e1 = room.AttentionQueue[0];
        Assert.Equal(("Una", 2, new MoneyRow(80m, "GBP"), 1L), (e1.Owner!.Name, e1.AgeDays, e1.Value, e1.RowVersion));
        Assert.Null(room.AttentionQueue[1].Owner);

        Assert.Equal(["GBP", "JPY"], room.Safeguarding.Select(s => s.Currency));
        var gbp = room.Safeguarding[0];
        Assert.Equal(new MoneyRow(20m, "GBP"), gbp.ApprovedAdjustments); // approved and unexpired only
        Assert.Equal(new MoneyRow(-80m, "GBP"), gbp.Result);
        Assert.Equal(new MoneyRow(-50m, "GBP"), gbp.PreviousResult);
        Assert.Equal(["stale balance"], gbp.Qualifications);
        Assert.Equal(["E-1"], gbp.ExceptionIds);
        var jpy = room.Safeguarding[1];
        Assert.Equal((new MoneyRow(0m, "JPY"), null), (jpy.ApprovedAdjustments, jpy.PreviousResult));

        Assert.Contains(room.Summary, s => s.Text.StartsWith("3 open exception(s), 1 blocking", StringComparison.Ordinal));
        Assert.Contains(room.Summary, s => s.LinkKind == "source" && s.LinkId == "D-2");
        Assert.Contains(room.Summary, s => s.LinkKind == "control" && s.Text.Contains("shortfall in GBP: -80.00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Control_room_of_a_run_without_data_is_empty()
    {
        var room = (await Repo.GetControlRoomAsync(EntityScope.Everything, "R-4"))!;
        Assert.Empty(room.EvidenceReadiness);
        Assert.Empty(room.ControlAreas);
        Assert.Empty(room.AttentionQueue);
        Assert.Empty(room.Safeguarding);
        Assert.Empty(room.Summary);
    }
}
