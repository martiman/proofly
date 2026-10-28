using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using System.Globalization;
using System.IO.Compression;
using Proofly.Connectors.Excel;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Ingestion;

/// <summary>Real Excel adapters + registry + ingestor + job processor over an in-test delivery set (neutral GBP/EUR/JPY data).</summary>
public sealed class XlsxEndToEndTests : IAsyncLifetime
{
    private readonly SchemaFixture _f = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-xlsx-" + Guid.NewGuid().ToString("N"));

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

    private static readonly string[] TrfHeader = "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference".Split(',');
    // amount cells are numeric (decimal text stored as the number); everything else is text
    private static object?[] Row(string n, string id, string client, decimal amount, string ccy) =>
        ["SRC-" + n, id, client == "" ? null : client, "LE-1", "BANK_TRANSFER", amount, ccy, "2026-10-27T08:15:00+01:00", "2026-10-27", "SUBMITTED", null, null];

    private static readonly string[] ManifestHeader = "source_name,delivery_id,business_date,schema_version,file_name,declared_record_count,declared_total_gbp,declared_total_eur,declared_total_jpy,expected_by,received_at,correction_of_delivery_id".Split(',');
    private static readonly object?[] ManifestRow = ["transfer_instructions", "DEL-TRF-1", "2026-10-27", "1.0", "transfer_instructions.xlsx", 6, 13.5m, 20m, 300m, "2026-10-28T06:00:00+01:00", "2026-10-28T05:42:00+01:00", null];

    /// <summary>Minimal Open XML workbook: one sheet, text as inline strings, decimals as numeric cells with the exact text.</summary>
    private static byte[] Xlsx(string[] header, params object?[][] rows)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string xml) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(xml); }
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Entry("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Entry("_rels/.rels", $"<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Entry("xl/workbook.xml", $"<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{rel}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Entry("xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            var sb = new System.Text.StringBuilder("<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            var all = new List<object?[]> { header.Cast<object?>().ToArray() };
            all.AddRange(rows);
            for (var r = 0; r < all.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (var c = 0; c < all[r].Length; c++)
                {
                    var addr = (char)('A' + c) + (r + 1).ToString();
                    switch (all[r][c])
                    {
                        case null: break;
                        case string t: sb.Append($"<c r=\"{addr}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(t)}</t></is></c>"); break;
                        case decimal d: sb.Append($"<c r=\"{addr}\"><v>{d.ToString(CultureInfo.InvariantCulture)}</v></c>"); break;
                        case int i: sb.Append($"<c r=\"{addr}\"><v>{i}</v></c>"); break;
                        default: throw new NotSupportedException();
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            Entry("xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Xlsx_delivery_set_ingests_end_to_end_through_the_job_processor()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Evidence:Directory"] = _dir }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton(_f.DataSource);
        sc.AddSingleton<Microsoft.Extensions.Logging.ILogger<JobProcessor>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>.Instance);
        sc.AddAudit();
        sc.AddExcelAdapters();
        sc.AddIngestion(cfg);
        await using var sp = sc.BuildServiceProvider();
        var adapters = sp.GetRequiredService<ISourceAdapterRegistry>();

        // the manifest itself goes through the Excel manifest adapter
        var manifestAdapter = adapters.Resolve<DeliveryManifestEntry>(SourceNames.DeliveryManifest, SourceFormat.Xlsx);
        var md = new SourceDescriptor(SourceNames.DeliveryManifest, SourceFormat.Xlsx, "1.0", "DEL-TRF-1", new DateOnly(2026, 10, 27), "delivery_manifest.xlsx");
        var manifestBytes = Xlsx(ManifestHeader, ManifestRow);
        Assert.True((await manifestAdapter.ValidateSchemaAsync(new MemoryStream(manifestBytes), md)).IsValid);
        DeliveryManifestEntry? manifest = null;
        await foreach (var r in manifestAdapter.ParseAsync(new MemoryStream(manifestBytes), md)) manifest = r.Record;
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.DeclaredTotals.Count);

        var file = Xlsx(TrfHeader,
            Row("001", "TRF-1", "CA-1", 10.50m, "GBP"),
            Row("002", "TRF-2", "CA-2", 20m, "EUR"),
            Row("003", "TRF-3", "CA-3", 300m, "JPY"),
            Row("004", "TRF-4", "", 1m, "GBP"),            // REQUIRED_FIELD_MISSING (amount still counts toward the declared total)
            Row("005", "TRF-5", "CA-5", 1.0000001m, "GBP"), // INVALID_VALUE: excess precision (excel float precision), no control amount
            Row("006", "TRF-1", "CA-1", 2m, "GBP"));       // DUPLICATE_RECORD by the framework
        var evidence = sp.GetRequiredService<EvidenceStore>();
        var obj = await evidence.WriteAsync(new MemoryStream(file));
        await sp.GetRequiredService<DeliveryRegistry>().RegisterAsync(new DeliveryRegistration(manifest, obj.Sha256, SourceFormat.Xlsx, "LE-1"));

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
