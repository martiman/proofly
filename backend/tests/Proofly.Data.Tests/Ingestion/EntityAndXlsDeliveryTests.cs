using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Excel;
using Proofly.Connectors.Csv;
using Proofly.Connectors.Tests.Excel;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Ingestion;

/// <summary>Registration is per legal entity (fix wave): two entities deliver the same source/date; and legacy .xls is registrable.</summary>
public sealed class EntityAndXlsDeliveryTests : IAsyncLifetime
{
    private readonly SchemaFixture _f = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-ent-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Day = new(2026, 10, 27);

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-1','One','EUR'),('LE-2','Two','EUR') ON CONFLICT DO NOTHING");
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        await _f.DisposeAsync();
    }

    private ServiceProvider Services(Action<IServiceCollection> adapters)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Evidence:Directory"] = _dir }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton(_f.DataSource);
        sc.AddSingleton<Microsoft.Extensions.Logging.ILogger<JobProcessor>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>.Instance);
        sc.AddAudit();
        adapters(sc);
        sc.AddIngestion(cfg);
        return sc.BuildServiceProvider();
    }

    private static readonly string[] TrfHeader = "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference".Split(',');

    private static string Csv(string le) => string.Join("\n", string.Join(',', TrfHeader),
        $"SRC-1,TRF-1,CA-1,{le},BANK_TRANSFER,10.50,GBP,2026-10-27T08:15:00+01:00,2026-10-27,SUBMITTED,,") + "\n";

    private static DeliveryManifestEntry Manifest(string id, string? correctionOf = null, int? seq = null, string? le = null, string file = "t.csv") =>
        new("transfer_instructions", id, Day, "1.0", file, 1, [new Money(10.50m, "GBP")], null, null, correctionOf, seq, le);

    [Fact]
    public async Task Two_entities_register_ingest_and_correct_the_same_source_and_date_independently()
    {
        await using var sp = Services(s => s.AddCsvAdapters());
        var reg = sp.GetRequiredService<DeliveryRegistry>();
        var ev = sp.GetRequiredService<EvidenceStore>();
        var same = (await ev.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes(Csv("LE-1"))))).Sha256;

        var a = await reg.RegisterAsync(new DeliveryRegistration(Manifest("DEL-A", seq: 1), same, SourceFormat.Csv, "LE-1"));
        // identical content (same fingerprint), same source/date, other entity: not a duplicate, not a missing correction link, own sequence
        var b = await reg.RegisterAsync(new DeliveryRegistration(Manifest("DEL-B", seq: 1), same, SourceFormat.Csv, "LE-2"));
        Assert.Empty(a.Failures);
        Assert.Empty(b.Failures);
        Assert.Equal(DeliveryState.Received, b.State);

        // within an entity the checks still hold
        var dup = await reg.RegisterAsync(new DeliveryRegistration(Manifest("DEL-C", correctionOf: "DEL-A"), same, SourceFormat.Csv, "LE-1"));
        Assert.Contains(dup.Failures, f => f.ReasonCode == ReasonCodes.DuplicateDelivery);
        var noLink = await reg.RegisterAsync(new DeliveryRegistration(Manifest("DEL-D"), (await ev.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes(Csv("LE-1") + "\n")))).Sha256, SourceFormat.Csv, "LE-1"));
        Assert.Contains(noLink.Failures, f => f.ReasonCode == ReasonCodes.CorrectionLinkMissing);
        // a correction cannot name another entity's delivery
        var foreign = await reg.RegisterAsync(new DeliveryRegistration(Manifest("DEL-E", correctionOf: "DEL-B"),
            (await ev.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes(Csv("LE-1") + "\n\n")))).Sha256, SourceFormat.Csv, "LE-1"));
        Assert.Contains(foreign.Failures, f => f.ReasonCode == ReasonCodes.CorrectionLinkMissing);

        Assert.Equal("LE-1", (await reg.GetAsync("DEL-A"))!.LegalEntityId);
        Assert.Equal("LE-2", (await reg.GetAsync("DEL-B"))!.LegalEntityId);

        // both ingest; finalisation is also per entity
        Assert.True(await sp.GetRequiredService<JobProcessor>().ProcessOneAsync("w", default));
        Assert.True(await sp.GetRequiredService<JobProcessor>().ProcessOneAsync("w", default));
        Assert.Equal(2, await _f.Scalar<long>("SELECT count(*) FROM source_deliveries WHERE state IN ('RECEIVED','QUALIFIED') AND ingested_at IS NOT NULL"));

        // the manifest's optional legal_entity_id must agree with the registration
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reg.RegisterAsync(new DeliveryRegistration(Manifest("DEL-F", le: "LE-2"), same, SourceFormat.Csv, "LE-1")));
    }

    [Fact]
    public async Task Manifest_schema_reads_the_optional_legal_entity_column()
    {
        await using var sp = Services(s => s.AddCsvAdapters());
        var ad = sp.GetRequiredService<ISourceAdapterRegistry>().Resolve<DeliveryManifestEntry>(SourceNames.DeliveryManifest, SourceFormat.Csv);
        var d = new SourceDescriptor(SourceNames.DeliveryManifest, SourceFormat.Csv, "1.0", "X", Day, "m.csv");
        var bytes = Encoding.UTF8.GetBytes("source_name,delivery_id,business_date,schema_version,file_name,legal_entity_id\ntransfer_instructions,DEL-9,2026-10-27,1.0,t.csv,LE-2\n");
        DeliveryManifestEntry? m = null;
        await foreach (var r in ad.ParseAsync(new MemoryStream(bytes), d)) m = r.Record;
        Assert.Equal("LE-2", m!.LegalEntityId);
    }

    [Fact]
    public async Task Xls_delivery_registers_and_ingests_end_to_end()
    {
        await using var sp = Services(s => s.AddExcelAdapters());
        var sheet = new SheetData("Data", [
            TrfHeader.Cast<object?>().ToArray(),
            ["SRC-1", "TRF-1", "CA-1", "LE-1", "BANK_TRANSFER", 10.5, "GBP", "2026-10-27T08:15:00+01:00", "2026-10-27", "SUBMITTED", null, null]]);
        var bytes = Workbooks.Xls(sheet);
        Assert.Equal(SourceFormat.Xls, WorkbookFormat.Sniff(new MemoryStream(bytes)));
        var obj = await sp.GetRequiredService<EvidenceStore>().WriteAsync(new MemoryStream(bytes));
        var res = await sp.GetRequiredService<DeliveryRegistry>().RegisterAsync(
            new DeliveryRegistration(Manifest("DEL-XLS", file: "t.xls"), obj.Sha256, SourceFormat.Xls, "LE-1"));
        Assert.Empty(res.Failures);
        Assert.True(await sp.GetRequiredService<JobProcessor>().ProcessOneAsync("w", default));
        var snap = (await sp.GetRequiredService<DeliveryRegistry>().GetAsync("DEL-XLS"))!;
        Assert.Equal(SourceFormat.Xls, snap.Format);
        Assert.Equal(DeliveryState.Received, snap.State);
        Assert.Equal(1, snap.AcceptedRecordCount);
        Assert.Equal("XLS", await _f.Scalar<string>("SELECT source_format FROM source_deliveries WHERE id='DEL-XLS'"));
    }
}
