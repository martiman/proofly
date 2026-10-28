using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Csv;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Ingestion;

/// <summary>Real CSV adapters + registry + ingestor + job processor over an in-test delivery set (neutral GBP/EUR/JPY data).</summary>
public sealed class CsvEndToEndTests : IAsyncLifetime
{
    private readonly SchemaFixture _f = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-csv-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-1','Test','EUR') ON CONFLICT DO NOTHING");
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        await _f.DisposeAsync();
    }

    private const string TrfHeader = "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference";
    private static string Row(string n, string id, string client, string amount, string ccy) =>
        $"SRC-{n},{id},{client},LE-1,BANK_TRANSFER,{amount},{ccy},2026-10-27T08:15:00+01:00,2026-10-27,SUBMITTED,,";

    private const string ManifestHeader = "source_name,delivery_id,business_date,schema_version,file_name,declared_record_count,declared_total_gbp,declared_total_eur,declared_total_jpy,expected_by,received_at,correction_of_delivery_id";
    private const string ManifestRow = "transfer_instructions,DEL-TRF-1,2026-10-27,1.0,transfer_instructions.csv,6,13.5,20,300,2026-10-28T06:00:00+01:00,2026-10-28T05:42:00+01:00,";

    [Fact]
    public async Task Csv_delivery_set_ingests_end_to_end_through_the_job_processor()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Evidence:Directory"] = _dir }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton(_f.DataSource);
        sc.AddSingleton<Microsoft.Extensions.Logging.ILogger<JobProcessor>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>.Instance);
        sc.AddAudit();
        sc.AddCsvAdapters();
        sc.AddIngestion(cfg);
        await using var sp = sc.BuildServiceProvider();
        var adapters = sp.GetRequiredService<ISourceAdapterRegistry>();

        // the manifest itself goes through the CSV manifest adapter
        var manifestAdapter = adapters.Resolve<DeliveryManifestEntry>(SourceNames.DeliveryManifest, SourceFormat.Csv);
        var md = new SourceDescriptor(SourceNames.DeliveryManifest, SourceFormat.Csv, "1.0", "DEL-TRF-1", new DateOnly(2026, 10, 27), "delivery_manifest.csv");
        var manifestBytes = Encoding.UTF8.GetBytes(ManifestHeader + "\n" + ManifestRow + "\n");
        Assert.True((await manifestAdapter.ValidateSchemaAsync(new MemoryStream(manifestBytes), md)).IsValid);
        DeliveryManifestEntry? manifest = null;
        await foreach (var r in manifestAdapter.ParseAsync(new MemoryStream(manifestBytes), md)) manifest = r.Record;
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.DeclaredTotals.Count);

        var file = Encoding.UTF8.GetBytes(string.Join("\n", TrfHeader,
            Row("001", "TRF-1", "CA-1", "10.50", "GBP"),
            Row("002", "TRF-2", "CA-2", "20", "EUR"),
            Row("003", "TRF-3", "CA-3", "300", "JPY"),
            Row("004", "TRF-4", "", "1", "GBP"),            // REQUIRED_FIELD_MISSING (amount still counts toward the declared total)
            Row("005", "TRF-5", "CA-5", "1.0000001", "GBP"), // INVALID_VALUE: excess precision, no control amount
            Row("006", "TRF-1", "CA-1", "2", "GBP")) + "\n"); // DUPLICATE_RECORD by the framework
        var evidence = sp.GetRequiredService<EvidenceStore>();
        var obj = await evidence.WriteAsync(new MemoryStream(file));
        await sp.GetRequiredService<DeliveryRegistry>().RegisterAsync(new DeliveryRegistration(manifest, obj.Sha256, SourceFormat.Csv, "LE-1"));

        Assert.True(await sp.GetRequiredService<JobProcessor>().ProcessOneAsync("w1", default));

        Assert.Equal("SUCCEEDED", await _f.Scalar<string>("SELECT state FROM jobs WHERE type='delivery.ingest'"));
        Assert.Equal("QUALIFIED", await _f.Scalar<string>("SELECT state FROM source_deliveries WHERE id='DEL-TRF-1'"));
        Assert.Equal(3, await _f.Scalar<long>("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(3, await _f.Scalar<long>("SELECT count(*) FROM delivery_rejections"));
        Assert.Equal(ReasonCodes.RequiredFieldMissing, await _f.Scalar<string>("SELECT reason_code FROM delivery_rejections WHERE source_row_id='SRC-004'"));
        Assert.Equal(ReasonCodes.InvalidValue, await _f.Scalar<string>("SELECT reason_code FROM delivery_rejections WHERE source_row_id='SRC-005'"));
        Assert.Equal(ReasonCodes.DuplicateRecord, await _f.Scalar<string>("SELECT reason_code FROM delivery_rejections WHERE source_row_id='SRC-006'"));
        // control totals over ALL rows that carry a valid amount: GBP 10.5 + 1 + 2, EUR 20, JPY 300 = the declared totals
        foreach (var (ccy, amount) in new[] { ("GBP", 13.5m), ("EUR", 20m), ("JPY", 300m) })
        {
            Assert.Equal(amount, await _f.Scalar<decimal>($"SELECT declared_amount FROM delivery_control_totals WHERE currency='{ccy}'"));
            Assert.Equal(amount, await _f.Scalar<decimal>($"SELECT actual_amount FROM delivery_control_totals WHERE currency='{ccy}'"));
        }
        var snap = (await sp.GetRequiredService<DeliveryRegistry>().GetAsync("DEL-TRF-1"))!;
        Assert.Equal(6, snap.TotalRowCount);
        Assert.Equal(3, snap.AcceptedRecordCount);
        Assert.Equal(3, snap.RejectedRecordCount);
    }
}
