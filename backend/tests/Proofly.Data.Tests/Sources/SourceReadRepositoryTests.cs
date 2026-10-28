using Proofly.Data.Audit;
using Proofly.Data.Sources;

namespace Proofly.Data.Tests.Sources;

public sealed class SourceReadRepositoryTests : IAsyncLifetime
{
    private readonly SchemaFixture _f = new();
    private SourceReadRepository _repo = null!;
    private static readonly EntityScope All = EntityScope.Everything;

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec(SourceSeed.Sql);
        _repo = new SourceReadRepository(_f.DataSource);
        var writer = new AuditWriter();
        await using var conn = await _f.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await writer.AppendAsync(tx, new AuditEventInput("DELIVERY_RECEIVED", AuditActor.System("ingestion"), "DELIVERY", "D1", "registered",
            AfterJson: """{"state":"RECEIVED"}"""));
        await writer.AppendAsync(tx, new AuditEventInput("DELIVERY_RECEIVED", AuditActor.System("ingestion"), "DELIVERY", "D1", "ingested",
            AfterJson: """{"state":"QUALIFIED","acceptedRecords":3}"""));
        await writer.AppendAsync(tx, new AuditEventInput("RECORD_REJECTED", AuditActor.System("ingestion"), "DELIVERY", "D1", "2 rows rejected"));
        await writer.AppendAsync(tx, new AuditEventInput("DELIVERY_INGESTION_RETRIED", AuditActor.System("ingestion"), "DELIVERY", "D1", "retried"));
        await writer.AppendAsync(tx, new AuditEventInput("RUN_STARTED", AuditActor.System("x"), "DELIVERY", "D1", "ignored"));
        await writer.AppendAsync(tx, new AuditEventInput("DELIVERY_RECEIVED", AuditActor.System("ingestion"), "DELIVERY", "D3", "other"));
        await tx.CommitAsync();
    }

    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task Run_deliveries_carry_per_currency_totals_findings_and_qualifications()
    {
        var items = (await _repo.ListRunDeliveriesAsync("RUN-A", All))!;
        Assert.Equal(["D2", "D1"], items.Select(i => i.Id)); // ordered by source name
        var d1 = items.Single(i => i.Id == "D1");
        Assert.Equal([("EUR", 20.123456m), ("GBP", 150.50m), ("JPY", 3000m)], d1.DeclaredTotals.Select(t => (t.Currency, t.Amount)));
        Assert.Equal([("GBP", 150.50m), ("JPY", 3000m)], d1.ActualTotals.Select(t => (t.Currency, t.Amount)));
        Assert.Equal(["F-1"], d1.FindingIds);
        Assert.Equal(["INVALID_VALUE: 1 row(s)", "REQUIRED_FIELD_MISSING: 1 row(s)"], d1.Qualifications);
        Assert.Null(d1.SupersededByDeliveryId);
        Assert.Equal("D1", items.Single(i => i.Id == "D2").CorrectionOfDeliveryId);
        Assert.Null(items.Single(i => i.Id == "D2").ExpectedBy);
    }

    [Fact]
    public async Task Out_of_scope_or_unknown_is_null_everywhere()
    {
        var a = EntityScope.Only("LE-A");
        Assert.Null(await _repo.ListRunDeliveriesAsync("RUN-B", a));
        Assert.Null(await _repo.ListRunDeliveriesAsync("NOPE", All));
        Assert.Null(await _repo.GetDeliveryAsync("D3", a));
        Assert.Null(await _repo.ListRecordsAsync("D3", new RecordQuery(null, null, 10, null, null, false), a));
        Assert.Null(await _repo.GetProcessingHistoryAsync("D3", a));
        var d3rec = (await _repo.ListRecordsAsync("D3", new RecordQuery(null, null, 10, null, null, false), All))!.Items.Single().RecordId;
        Assert.Null(await _repo.GetRecordAsync(d3rec, a));
        Assert.NotNull(await _repo.GetRecordAsync(d3rec, EntityScope.Only("LE-B")));
        Assert.NotNull(await _repo.GetDeliveryAsync("D1", a));
        Assert.Null(await _repo.GetDeliveryAsync("D1", EntityScope.Only()));
    }

    [Fact]
    public async Task Detail_has_failures_and_version()
    {
        var d = (await _repo.GetDeliveryAsync("D2", All))!;
        Assert.Equal("REJECTED", d.Delivery.State);
        Assert.Equal(["DUPLICATE_DELIVERY"], d.Failures.Select(f => f.ReasonCode));
        Assert.True(d.Delivery.Version >= 1);
        Assert.Equal(7, (await _repo.GetDeliveryAsync("D1", All))!.Delivery.SequenceNumber);
    }

    [Fact]
    public async Task Records_page_across_cursors_accepted_first_then_rejected()
    {
        var seen = new List<string>();
        int? g = null; long? o = null;
        for (var i = 0; i < 10; i++)
        {
            var page = (await _repo.ListRecordsAsync("D1", new RecordQuery(null, null, 2, g, o, i == 0), All))!;
            if (i == 0) { Assert.Equal(5, page.Total); }
            var take = page.Items.Take(2).ToList();
            seen.AddRange(take.Select(r => r.SourceRowId));
            if (page.Items.Count <= 2) { break; }
            g = take[^1].Group; o = take[^1].Ordinal;
        }
        Assert.Equal(["s1", "s2", "s3", "s4", ""], seen);
    }

    [Fact]
    public async Task Record_content_amounts_stay_decimal_strings_and_rejections_carry_reasons()
    {
        var all = (await _repo.ListRecordsAsync("D1", new RecordQuery(null, null, 50, null, null, false), All))!.Items;
        var s1 = all[0];
        Assert.True(s1.Accepted);
        Assert.Equal(1, s1.RowNumber);
        Assert.Equal("100.000000", s1.Fields["amount"]!.GetValue<string>());
        Assert.Equal("GBP", s1.Fields["currency"]!.GetValue<string>());
        Assert.DoesNotContain("delivery_id", s1.Fields.Select(k => k.Key));
        Assert.Equal(["F-1"], s1.FindingIds);
        Assert.Equal(64, s1.ContentFingerprint!.Length);
        var rej = all.Where(r => !r.Accepted).ToList();
        Assert.Equal([4, 5], rej.Select(r => r.RowNumber));
        Assert.Equal(["INVALID_VALUE: amount is not a decimal"], rej[0].RejectionReasons);
        Assert.Contains("abc", rej[0].Fields["raw_row"]!.GetValue<string>());
        Assert.Empty(rej[0].FindingIds);
    }

    [Fact]
    public async Task Accepted_filter_and_search()
    {
        var rejected = (await _repo.ListRecordsAsync("D1", new RecordQuery(false, null, 50, null, null, true), All))!;
        Assert.Equal(2, rejected.Total);
        Assert.All(rejected.Items, r => Assert.False(r.Accepted));
        var accepted = (await _repo.ListRecordsAsync("D1", new RecordQuery(true, null, 50, null, null, false), All))!;
        Assert.Equal(["s1", "s2", "s3"], accepted.Items.Select(r => r.SourceRowId));
        var jpy = (await _repo.ListRecordsAsync("D1", new RecordQuery(null, "jpy", 50, null, null, false), All))!;
        Assert.Equal(["s3"], jpy.Items.Select(r => r.SourceRowId));
        Assert.Empty((await _repo.ListRecordsAsync("D1", new RecordQuery(null, "100%", 50, null, null, false), All))!.Items);
    }

    [Fact]
    public async Task Delivery_without_a_table_or_records_is_empty()
    {
        var p = (await _repo.ListRecordsAsync("D2", new RecordQuery(null, null, 10, null, null, true), All))!;
        Assert.Empty(p.Items);
        Assert.Equal(0, p.Total);
    }

    [Fact]
    public async Task Single_record_round_trips_and_bad_ids_are_null()
    {
        var all = (await _repo.ListRecordsAsync("D1", new RecordQuery(null, null, 50, null, null, false), All))!.Items;
        foreach (var r in all)
        {
            var got = (await _repo.GetRecordAsync(r.RecordId, All))!.Value;
            Assert.Equal(r.RecordId, got.Record.RecordId);
            Assert.Equal(r.SourceRowId, got.Record.SourceRowId);
            Assert.Equal("D1", got.Delivery.Id);
        }
        foreach (var bad in new[] { "", "x", "rejected:abc", "transfer_instructions:999999", "rejected:999999", "nope:1", "banking_transactions:1", "rejected:-1" })
        {
            Assert.Null(await _repo.GetRecordAsync(bad, All));
        }
    }

    [Fact]
    public async Task Processing_history_comes_from_the_deliverys_audit_events()
    {
        var steps = (await _repo.GetProcessingHistoryAsync("D1", All))!;
        Assert.Equal([("RECEIVED", "OK"), ("ACCEPTED", "WARNING"), ("RECORDS_PARSED", "WARNING"), ("RETRIED", "OK")], steps.Select(s => (s.Stage, s.Outcome)));
        Assert.All(steps, s => Assert.Equal("SYSTEM", s.ActorType));
        Assert.Empty((await _repo.GetProcessingHistoryAsync("D2", All))!);
    }
}
