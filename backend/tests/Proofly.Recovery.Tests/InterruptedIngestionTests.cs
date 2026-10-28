using Proofly.Data;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Domain.Enums;
using Proofly.Recovery.Tests.Support;

namespace Proofly.Recovery.Tests;

/// <summary>
/// Acceptance: interrupted ingestion restarts without duplicates (omvp-acceptance-plan, Reliability and recovery).
/// A killed worker is simulated three ways: the parse stops after N rows, the processing token is cancelled after N rows,
/// and the database transaction fails after the canonical rows were written. After every kind of death the restarted
/// ingestion must leave exactly what an undisturbed ingestion leaves.
/// </summary>
public sealed class InterruptedIngestionTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 10, 27);
    private static readonly TransferFile File = TransferFile.Build("2026-10-27");
    private RecoveryDb _db = null!, _reference = null!;
    private readonly string _dir = TempDir.Create("proofly-rec-ing"), _refDir = TempDir.Create("proofly-rec-ing-ref");

    public async Task InitializeAsync()
    {
        _db = await RecoveryDb.CreateSchemaAsync();
        _reference = await RecoveryDb.CreateSchemaAsync();
        foreach (var d in new[] { _db, _reference })
            await d.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-CZ-001','Test','EUR')");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _reference.DisposeAsync();
        TempDir.Delete(_dir);
        TempDir.Delete(_refDir);
    }

    /// <summary>Everything ingestion writes, in a form that can be compared between two databases.</summary>
    private static async Task<string> Footprint(RecoveryDb db)
    {
        var parts = new List<string>
        {
            "records=" + await db.Scalar<string>("SELECT coalesce(string_agg(row_number||':'||source_row_id||':'||amount||currency, ',' ORDER BY row_number), '') FROM transfer_instructions"),
            "rejections=" + await db.Scalar<string>("SELECT coalesce(string_agg(row_number||':'||reason_code||':'||coalesce(source_row_id,''), ',' ORDER BY row_number, reason_code), '') FROM delivery_rejections"),
            "totals=" + await db.Scalar<string>("SELECT coalesce(string_agg(currency||':'||declared_amount||':'||actual_amount, ',' ORDER BY currency), '') FROM delivery_control_totals"),
            "delivery=" + await db.Scalar<string>("SELECT state||':'||accepted_record_count||':'||rejected_record_count||':'||total_row_count||':'||(ingested_at IS NOT NULL) FROM source_deliveries"),
            "audit=" + await db.Scalar<string>("SELECT string_agg(event_type||'x'||n, ',' ORDER BY event_type) FROM (SELECT event_type, count(*) n FROM audit_events GROUP BY event_type) t"),
        };
        return string.Join("\n", parts);
    }

    private async Task<string> CleanFootprint()
    {
        using var rig = new Rig(_reference, _refDir);
        await rig.RegisterAsync(File, "DEL-TRF-01", Day);
        await rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");
        return await Footprint(_reference);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    public async Task Worker_dying_while_parsing_leaves_nothing_and_the_restart_ingests_once(int dieAfterRows)
    {
        var expected = await CleanFootprint();
        using (var first = new Rig(_db, _dir))
        {
            await first.RegisterAsync(File, "DEL-TRF-01", Day);
            first.Adapter.AfterRow = n => { if (n == dieAfterRows) throw new IOException("worker process killed"); };
            await Assert.ThrowsAsync<IOException>(() => first.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01"));
        }
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM delivery_rejections"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM delivery_control_totals WHERE actual_amount IS NOT NULL"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='RECORD_REJECTED'"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='DELIVERY_RECEIVED'")); // registration only
        Assert.Equal("RECEIVED", await _db.Scalar<string>("SELECT state FROM source_deliveries"));

        await _db.Restart();
        using var restarted = new Rig(_db, _dir);
        var outcome = await restarted.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");

        Assert.Equal(DeliveryState.Qualified, outcome.State);
        Assert.Equal(expected, await Footprint(_db));
        Assert.True((await restarted.Get<IAuditVerifier>().VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Cancelling_the_processing_token_mid_file_leaves_nothing_and_a_retry_ingests_once()
    {
        var expected = await CleanFootprint();
        using var rig = new Rig(_db, _dir);
        await rig.RegisterAsync(File, "DEL-TRF-01", Day);
        using var cts = new CancellationTokenSource();
        rig.Adapter.AfterRow = n => { if (n == 7) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01", cts.Token));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM source_deliveries WHERE ingested_at IS NOT NULL"));

        rig.Adapter.AfterRow = null;
        await rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");
        Assert.Equal(expected, await Footprint(_db));
    }

    [Theory]
    [InlineData(1)] // after the canonical rows and rejections were inserted, while writing DELIVERY_RECEIVED
    [InlineData(2)] // DELIVERY_RECEIVED already written in the open transaction, RECORD_REJECTED #1 fails
    [InlineData(3)] // last audit event of the delivery fails
    public async Task Failure_after_rows_were_written_rolls_everything_back_and_the_retry_has_no_duplicates(int failOnAudit)
    {
        var expected = await CleanFootprint();
        using (var setup = new Rig(_db, _dir)) await setup.RegisterAsync(File, "DEL-TRF-01", Day);

        using (var dying = new Rig(_db, _dir, failAuditOn: failOnAudit))
            await Assert.ThrowsAsync<InvalidOperationException>(() => dying.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01"));

        Assert.Equal(0, await _db.Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM delivery_rejections"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM delivery_control_totals WHERE actual_amount IS NOT NULL"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='DELIVERY_RECEIVED'"));
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='RECORD_REJECTED'"));

        await _db.Restart();
        using var restarted = new Rig(_db, _dir);
        await restarted.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");
        Assert.Equal(expected, await Footprint(_db));
        Assert.True((await restarted.Get<IAuditVerifier>().VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Repeating_a_finished_ingestion_changes_nothing()
    {
        using var rig = new Rig(_db, _dir);
        await rig.RegisterAsync(File, "DEL-TRF-01", Day);
        await rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");
        var once = await Footprint(_db);

        var again = await rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");

        Assert.True(again.AlreadyIngested);
        Assert.Equal(once, await Footprint(_db));
    }

    [Fact]
    public async Task Registering_the_same_delivery_again_after_a_restart_creates_no_second_delivery_or_job()
    {
        using (var rig = new Rig(_db, _dir)) await rig.RegisterAsync(File, "DEL-TRF-01", Day);
        await _db.Restart();
        using var restarted = new Rig(_db, _dir);
        var again = await restarted.RegisterAsync(File, "DEL-TRF-01", Day);

        Assert.False(again.Created);
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM source_deliveries"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM jobs WHERE type='delivery.ingest'"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='DELIVERY_RECEIVED'"));
    }
}
