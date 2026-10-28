using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Xml;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Ingestion;

/// <summary>Real CSV adapters + registry + ingestor + job processor over an in-test delivery set (neutral GBP/EUR/JPY data).</summary>
public sealed class XmlEndToEndTests : IAsyncLifetime
{
    private readonly SchemaFixture _f = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-xml-" + Guid.NewGuid().ToString("N"));

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

    private static string Rec(string n, string id, string client, string amount, string ccy) =>
        $"<record><source_row_id>SRC-{n}</source_row_id><instruction_id>{id}</instruction_id>" + (client.Length == 0 ? "" : $"<client_account_id>{client}</client_account_id>") +
        $"<legal_entity_id>LE-1</legal_entity_id><transaction_type>BANK_TRANSFER</transaction_type><amount>{amount}</amount><currency>{ccy}</currency>" +
        "<instruction_timestamp>2026-10-27T08:15:00+01:00</instruction_timestamp><requested_value_date>2026-10-27</requested_value_date><source_status>SUBMITTED</source_status></record>";

    private static string Delivery(string source, string records) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<delivery source=\"{source}\" schemaVersion=\"1.0\" businessDate=\"2026-10-27\">\n{records}\n</delivery>\n";

    private const string ManifestRecord = "<record><source_name>transfer_instructions</source_name><delivery_id>DEL-TRF-1</delivery_id><business_date>2026-10-27</business_date><schema_version>1.0</schema_version><file_name>transfer_instructions.xml</file_name><declared_record_count>6</declared_record_count><declared_total_gbp>13.5</declared_total_gbp><declared_total_eur>20</declared_total_eur><declared_total_jpy>300</declared_total_jpy><expected_by>2026-10-28T06:00:00+01:00</expected_by><received_at>2026-10-28T05:42:00+01:00</received_at></record>";

    [Fact]
    public async Task Xml_delivery_set_ingests_end_to_end_through_the_job_processor()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Evidence:Directory"] = _dir }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton(_f.DataSource);
        sc.AddSingleton<Microsoft.Extensions.Logging.ILogger<JobProcessor>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>.Instance);
        sc.AddAudit();
        sc.AddXmlAdapters();
        sc.AddIngestion(cfg);
        await using var sp = sc.BuildServiceProvider();
        var adapters = sp.GetRequiredService<ISourceAdapterRegistry>();

        // the manifest itself goes through the XML manifest adapter
        var manifestAdapter = adapters.Resolve<DeliveryManifestEntry>(SourceNames.DeliveryManifest, SourceFormat.Xml);
        var md = new SourceDescriptor(SourceNames.DeliveryManifest, SourceFormat.Xml, "1.0", "DEL-TRF-1", new DateOnly(2026, 10, 27), "delivery_manifest.xml");
        var manifestBytes = Encoding.UTF8.GetBytes(Delivery(SourceNames.DeliveryManifest, ManifestRecord));
        Assert.True((await manifestAdapter.ValidateSchemaAsync(new MemoryStream(manifestBytes), md)).IsValid);
        DeliveryManifestEntry? manifest = null;
        await foreach (var r in manifestAdapter.ParseAsync(new MemoryStream(manifestBytes), md)) manifest = r.Record;
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.DeclaredTotals.Count);

        var file = Encoding.UTF8.GetBytes(Delivery(SourceNames.TransferInstructions, string.Join("\n",
            Rec("001", "TRF-1", "CA-1", "10.50", "GBP"),
            Rec("002", "TRF-2", "CA-2", "20", "EUR"),
            Rec("003", "TRF-3", "CA-3", "300", "JPY"),
            Rec("004", "TRF-4", "", "1", "GBP"),             // required element absent: REQUIRED_FIELD_MISSING (amount still counts toward the declared total)
            Rec("005", "TRF-5", "CA-5", "1.0000001", "GBP"), // XSD violation: INVALID_VALUE, no control amount
            Rec("006", "TRF-1", "CA-1", "2", "GBP"))));      // DUPLICATE_RECORD by the framework
        var evidence = sp.GetRequiredService<EvidenceStore>();
        var obj = await evidence.WriteAsync(new MemoryStream(file));
        await sp.GetRequiredService<DeliveryRegistry>().RegisterAsync(new DeliveryRegistration(manifest, obj.Sha256, SourceFormat.Xml, "LE-1"));

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
