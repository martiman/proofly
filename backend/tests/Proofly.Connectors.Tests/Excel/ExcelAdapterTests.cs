using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Excel;
using Proofly.Domain.Records;
using static Proofly.Connectors.Tests.Excel.ExcelSamples;

namespace Proofly.Connectors.Tests.Excel;

public class ExcelAdapterTests
{
    private const string Trf = SourceNames.TransferInstructions;
    private static Dictionary<string, object?> TrfRow => Typed(Trf);

    private static byte[] Book(SourceFormat f, params Dictionary<string, object?>[] rows) =>
        f == SourceFormat.Xls ? Workbooks.Xls(Sheet(Trf, rows)) : Workbooks.Xlsx(Sheet(Trf, rows));

    public static IEnumerable<object[]> SourcesAndFormats =>
        Samples.Headers.Keys.SelectMany(k => new[] { SourceFormat.Xlsx, SourceFormat.Xls }.Select(f => new object[] { k, f }));

    public static IEnumerable<object[]> Formats => [[SourceFormat.Xlsx], [SourceFormat.Xls]];

    [Theory, MemberData(nameof(SourcesAndFormats))]
    public async Task Every_source_parses_text_cells_exactly_like_the_csv_adapter(string source, SourceFormat format)
    {
        var sheet = Sheet(source, Typed(source));
        var bytes = format == SourceFormat.Xls ? Workbooks.Xls(sheet) : Workbooks.Xlsx(sheet);
        var xl = Assert.Single(await Parse(source, bytes, format));
        var csv = Assert.Single(await Samples.Parse(source, Samples.Csv(source, Samples.Row(source))));
        Assert.True(xl.IsAccepted, xl.Rejection?.Message);
        if (xl.Record is DeliveryManifestEntry xm)
        {
            var cm = (DeliveryManifestEntry)csv.Record!;
            Assert.Equal(cm.DeclaredTotals, xm.DeclaredTotals);
            Assert.Equal(cm with { DeclaredTotals = xm.DeclaredTotals }, xm);
        }
        else Assert.Equal(csv.Record, xl.Record);
        Assert.Equal(1, xl.RowNumber);
        Assert.True((await Validate(source, bytes, format)).IsValid);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Numeric_amount_cells_keep_their_exact_decimal_text(SourceFormat f)
    {
        var rows = new[]
        {
            TrfRow.With("amount", 100.5),
            TrfRow.With("amount", 5000).With("currency", "JPY"),
            TrfRow.With("amount", 123456.123456),
            TrfRow.With("amount", 0.000001),
        };
        var results = await Parse(Trf, Book(f, rows), f);
        Assert.All(results, r => Assert.True(r.IsAccepted, r.Rejection?.Message));
        var amounts = results.Select(r => ((TransferInstruction)r.Record!).Amount).ToArray();
        Assert.Equal([100.5m, 5000m, 123456.123456m, 0.000001m], amounts.Select(a => a.Amount));
        Assert.Equal("JPY", amounts[1].Currency);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Floats_that_are_not_exact_decimals_are_rejected_with_the_precision_reason(SourceFormat f)
    {
        var results = await Parse(Trf, Book(f, TrfRow.With("amount", 0.1 + 0.2), TrfRow.With("amount", 1.0 / 3.0), TrfRow.With("amount", 1.0000001)), f);
        Assert.All(results, r =>
        {
            Assert.False(r.IsAccepted);
            Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode);
            Assert.Equal("amount", r.Rejection.Field);
            Assert.Contains("excel float precision", r.Rejection.Message);
            Assert.Null(r.Rejection.ControlAmount);
        });
    }

    [Fact]
    public async Task A_precision_failure_does_not_lose_the_control_amount_of_a_different_valid_amount()
    {
        // Settlement: fees are imprecise; the control amount (gross_amount, text) is still reported for the control totals.
        var src = SourceNames.CardSettlements;
        var row = Typed(src).With("fees", 0.1 + 0.2);
        var r = Assert.Single(await Parse(src, Workbooks.Xlsx(Sheet(src, row))));
        Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode);
        Assert.Contains("excel float precision", r.Rejection.Message);
        Assert.Equal("S1", r.Rejection.SourceRowId);
        Assert.NotNull(r.Rejection.ControlAmount);
    }

    [Fact]
    public async Task Raw_numeric_text_with_seven_fraction_digits_is_rejected_and_six_is_accepted()
    {
        var bytes = Workbooks.Xlsx(Sheet(Trf, TrfRow.With("amount", new RawNumber("1.1234567")), TrfRow.With("amount", new RawNumber("1.123456")), TrfRow.With("amount", new RawNumber("8870.125"))));
        var r = await Parse(Trf, bytes);
        Assert.False(r[0].IsAccepted);
        Assert.True(r[1].IsAccepted, r[1].Rejection?.Message);
        Assert.Equal(8870.125m, ((TransferInstruction)r[2].Record!).Amount.Amount);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Text_amount_cells_are_handed_to_the_mapper_unchanged(SourceFormat f)
    {
        var r = await Parse(Trf, Book(f, TrfRow.With("amount", "7.25"), TrfRow.With("amount", " 7.25"), TrfRow.With("amount", "7,25")), f);
        Assert.Equal(7.25m, ((TransferInstruction)r[0].Record!).Amount.Amount);
        Assert.Equal(ReasonCodes.InvalidValue, r[1].Rejection!.ReasonCode);
        Assert.Equal(ReasonCodes.InvalidValue, r[2].Rejection!.ReasonCode);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Pure_date_cells_become_iso_dates_and_date_times_in_date_columns_are_rejected(SourceFormat f)
    {
        var r = await Parse(Trf, Book(f,
            TrfRow.With("requested_value_date", new DateTime(2026, 10, 27)),
            TrfRow.With("requested_value_date", new DateTime(2026, 10, 27, 13, 30, 0)),
            TrfRow.With("requested_value_date", 46318.0)), f);
        Assert.Equal(new DateOnly(2026, 10, 27), ((TransferInstruction)r[0].Record!).RequestedValueDate);
        Assert.Contains("time component", r[1].Rejection!.Message);
        Assert.Contains("numeric cell in Date column", r[2].Rejection!.Message);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Date_time_cells_in_timestamp_columns_need_a_configured_offset(SourceFormat f)
    {
        var row = TrfRow.With("instruction_timestamp", new DateTime(2026, 10, 27, 8, 15, 0));
        var none = Assert.Single(await Parse(Trf, Book(f, row), f));
        Assert.Equal(ReasonCodes.InvalidValue, none.Rejection!.ReasonCode);
        Assert.Equal("instruction_timestamp", none.Rejection.Field);
        Assert.Contains("DefaultTimeZoneOffset", none.Rejection.Message);

        var ok = Assert.Single(await Parse(Trf, Book(f, row), f, new ExcelSourceOptions { DefaultTimeZoneOffset = TimeSpan.FromHours(1) }));
        Assert.True(ok.IsAccepted, ok.Rejection?.Message);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 8, 15, 0, TimeSpan.FromHours(1)), ((TransferInstruction)ok.Record!).InstructionTimestamp);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Boolean_and_error_cells_are_rejected(SourceFormat f)
    {
        var r = await Parse(Trf, Book(f, TrfRow.With("source_status", true), TrfRow.With("amount", new ErrorCell("#DIV/0!"))), f);
        Assert.Equal(ReasonCodes.InvalidValue, r[0].Rejection!.ReasonCode);
        Assert.Contains("boolean", r[0].Rejection!.Message);
        Assert.Equal(ReasonCodes.InvalidValue, r[1].Rejection!.ReasonCode);
        Assert.Equal("amount", r[1].Rejection!.Field);
        Assert.Contains("error cell", r[1].Rejection!.Message);
    }

    [Fact]
    public async Task Formulas_use_the_cached_value_and_an_absent_cached_value_is_a_blank_required_cell()
    {
        var r = await Parse(Trf, Workbooks.Xlsx(Sheet(Trf,
            TrfRow.With("amount", new Formula("50+50.5", 100.5)),
            TrfRow.With("client_account_id", new Formula("\"CA-\"&\"1\"", "CA-1")),
            TrfRow.With("amount", new Formula("1+1", null)))));
        Assert.Equal(100.5m, ((TransferInstruction)r[0].Record!).Amount.Amount);
        Assert.True(r[1].IsAccepted, r[1].Rejection?.Message);
        Assert.Equal(ReasonCodes.RequiredFieldMissing, r[2].Rejection!.ReasonCode);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Numeric_cells_in_text_columns_are_rejected_unless_allowed(SourceFormat f)
    {
        var row = TrfRow.With("instruction_id", 12345);
        var strict = Assert.Single(await Parse(Trf, Book(f, row), f));
        Assert.Equal(ReasonCodes.InvalidValue, strict.Rejection!.ReasonCode);
        Assert.Contains("numeric cell in text column", strict.Rejection.Message);
        Assert.Equal("instruction_id", strict.Rejection.Field);

        var lax = await Parse(Trf, Book(f, row, TrfRow.With("instruction_id", 1.5)), f, new ExcelSourceOptions { AllowNumericText = true });
        Assert.Equal("12345", ((TransferInstruction)lax[0].Record!).InstructionId);
        Assert.False(lax[1].IsAccepted);
        // text identifiers keep leading zeros
        var text = Assert.Single(await Parse(Trf, Book(f, TrfRow.With("instruction_id", "00123")), f));
        Assert.Equal("00123", ((TransferInstruction)text.Record!).InstructionId);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Blank_rows_are_skipped_and_do_not_count(SourceFormat f)
    {
        var sheet = new SheetData("Data", [HeaderRow(Trf), null, Cells(Trf, TrfRow), null, null, Cells(Trf, TrfRow.With("amount", "x")), Enumerable.Repeat<object?>("", 12).ToArray(), Cells(Trf, TrfRow)]);
        var bytes = f == SourceFormat.Xls ? Workbooks.Xls(sheet) : Workbooks.Xlsx(sheet);
        var r = await Parse(Trf, bytes, f);
        Assert.Equal([1, 2, 3], r.Select(x => x.RowNumber));
        Assert.Equal([true, false, true], r.Select(x => x.IsAccepted));
    }

    [Fact]
    public async Task A_value_outside_the_header_range_rejects_the_row()
    {
        var cells = Cells(Trf, TrfRow).Append("stray").ToArray();
        var sheet = new SheetData("Data", [HeaderRow(Trf), cells]);
        var r = Assert.Single(await Parse(Trf, Workbooks.Xlsx(sheet)));
        Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode);
        Assert.Contains("outside the header", r.Rejection.Message);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Header_failures_are_reported_by_validation_and_throw_on_parse(SourceFormat f)
    {
        var headers = Samples.Headers[Trf];
        foreach (var bad in new[] { headers.Skip(1).ToArray(), [.. headers, "extra"], [.. headers, headers[0]], headers.Select(h => h == "amount" ? "Amount" : h).ToArray() })
        {
            var sheet = new SheetData("Data", [bad.Cast<object?>().ToArray()]);
            var bytes = f == SourceFormat.Xls ? Workbooks.Xls(sheet) : Workbooks.Xlsx(sheet);
            Assert.False((await Validate(Trf, bytes, f)).IsValid);
            await Assert.ThrowsAsync<SourceFormatException>(() => Parse(Trf, bytes, f));
        }
        var empty = f == SourceFormat.Xls ? Workbooks.Xls(new SheetData("Data", [])) : Workbooks.Xlsx(new SheetData("Data", []));
        Assert.False((await Validate(Trf, empty, f)).IsValid);
        await Assert.ThrowsAsync<SourceFormatException>(() => Parse(Trf, empty, f));
    }

    [Fact]
    public async Task Unsupported_schema_version_is_a_validation_failure()
    {
        var v = await Adapter(Trf, SourceFormat.Xlsx).ValidateSchemaAsync(new MemoryStream(Workbooks.Xlsx(Sheet(Trf, TrfRow))), Descriptor(Trf, SourceFormat.Xlsx) with { SchemaVersion = "9.9" });
        Assert.False(v.IsValid);
        Assert.Equal(ReasonCodes.SchemaUnsupported, v.ReasonCode);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task Sheet_name_and_header_row_are_configurable(SourceFormat f)
    {
        var data = new SheetData("Deliveries", [["note"], ["ignored"], HeaderRow(Trf), Cells(Trf, TrfRow)]);
        var other = new SheetData("Cover", [["nothing"]]);
        var bytes = f == SourceFormat.Xls ? Workbooks.Xls(other, data) : Workbooks.Xlsx(other, data);
        var r = Assert.Single(await Parse(Trf, bytes, f, new ExcelSourceOptions { SheetName = "Deliveries", HeaderRow = 3 }));
        Assert.True(r.IsAccepted, r.Rejection?.Message);
        // default = first worksheet, header on row 1: "Cover" is not a valid header
        await Assert.ThrowsAsync<SourceFormatException>(() => Parse(Trf, bytes, f));
        await Assert.ThrowsAsync<SourceFormatException>(() => Parse(Trf, bytes, f, new ExcelSourceOptions { SheetName = "Nope", HeaderRow = 3 }));
    }

    [Fact]
    public async Task Hidden_sheets_are_not_special_cased()
    {
        var hidden = Sheet(Trf, TrfRow) with { Hidden = true };
        var r = Assert.Single(await Parse(Trf, Workbooks.Xlsx(hidden)));
        Assert.True(r.IsAccepted);
    }

    [Fact]
    public async Task Manifest_dynamic_total_columns_accept_numeric_cells()
    {
        var src = SourceNames.DeliveryManifest;
        var row = Typed(src).With("declared_total_gbp", 121.75).With("declared_total_jpy", 5000);
        var r = Assert.Single(await Parse(src, Workbooks.Xlsx(Sheet(src, row))));
        Assert.True(r.IsAccepted, r.Rejection?.Message);
        var m = (DeliveryManifestEntry)r.Record!;
        Assert.Equal(2, m.DeclaredTotals.Count);
    }

    [Fact]
    public async Task A_20000_row_workbook_streams_through_without_buffering_the_records()
    {
        var path = Path.Combine(Path.GetTempPath(), "proofly-xlsx-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            const int n = 20_000;
            var rows = new List<object?[]?> { HeaderRow(Trf) };
            for (var i = 0; i < n; i++)
                rows.Add(Cells(Trf, TrfRow.With("source_row_id", "S" + i).With("instruction_id", "TRF-" + i).With("amount", i % 100 + 0.25)));
            await using (var fs = File.Create(path)) Workbooks.WriteXlsx(fs, new SheetData("Data", rows));
            rows = null;

            await using var read = File.OpenRead(path);
            var accepted = 0; var last = 0;
            await foreach (var r in Adapter(Trf, SourceFormat.Xlsx).ParseRecordsAsync(read, Descriptor(Trf, SourceFormat.Xlsx)))
            {
                Assert.True(r.IsAccepted, r.Rejection?.Message);
                accepted++; last = r.RowNumber;
            }
            Assert.Equal(n, accepted);
            Assert.Equal(n, last);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Corrupt_empty_wrong_format_and_password_protected_files_are_unreadable()
    {
        var good = Workbooks.Xlsx(Sheet(Trf, TrfRow));
        var truncated = good.Take(good.Length / 2).ToArray();
        var garbage = Enumerable.Range(0, 2000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var zipNotXlsx = new System.IO.MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(zipNotXlsx, System.IO.Compression.ZipArchiveMode.Create, true)) z.CreateEntry("hello.txt").Open().Dispose();

        foreach (var (bytes, format) in new[]
        {
            (truncated, SourceFormat.Xlsx), (garbage, SourceFormat.Xlsx), ([], SourceFormat.Xlsx), (zipNotXlsx.ToArray(), SourceFormat.Xlsx),
            (garbage, SourceFormat.Xls), (good, SourceFormat.Xls),                      // xlsx content given to the .xls adapter
            (Workbooks.Xls(Sheet(Trf, TrfRow)), SourceFormat.Xlsx),                      // xls content given to the .xlsx adapter
            (Workbooks.XlsPasswordProtected(), SourceFormat.Xls),
        })
        {
            await Assert.ThrowsAsync<SourceFormatException>(() => Parse(Trf, bytes, format));
            await Assert.ThrowsAsync<SourceFormatException>(() => Validate(Trf, bytes, format));
        }
    }

    [Fact]
    public async Task A_non_seekable_stream_is_supported()
    {
        var bytes = Workbooks.Xlsx(Sheet(Trf, TrfRow));
        var list = new List<ParseResult<ISourceRecord>>();
        await foreach (var r in Adapter(Trf, SourceFormat.Xlsx).ParseRecordsAsync(new ForwardOnly(bytes), Descriptor(Trf, SourceFormat.Xlsx))) list.Add(r);
        Assert.True(Assert.Single(list).IsAccepted);
    }

    private sealed class ForwardOnly(byte[] b) : Stream
    {
        private readonly MemoryStream _m = new(b);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _m.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Module_registers_xlsx_and_xls_adapters_for_every_source()
    {
        using var sp = new ServiceCollection().AddExcelAdapters(o => o.Sources[Trf] = new ExcelSourceOptions { HeaderRow = 2 }).BuildServiceProvider();
        var registry = new SourceAdapterRegistry(sp.GetServices<ISourceAdapter>());
        foreach (var s in SourceNames.DataSources.Append(SourceNames.DeliveryManifest))
            foreach (var f in new[] { SourceFormat.Xlsx, SourceFormat.Xls })
                Assert.Equal(f, registry.Resolve(s, f).Format);
        Assert.Equal(18, registry.Adapters.Count);
        Assert.Null(registry.Find(Trf, SourceFormat.Csv));
    }

    [Fact]
    public void Options_are_validated_and_formats_detected()
    {
        Assert.Throws<InvalidOperationException>(() => new ExcelSourceOptions { HeaderRow = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ExcelSourceOptions { DefaultTimeZoneOffset = TimeSpan.FromHours(15) }.Validate());
        Assert.Throws<ArgumentException>(() => new ExcelSourceAdapter<TransferInstruction>(Proofly.Connectors.Schemas.SourceSchemas.TransferInstructions, SourceFormat.Csv));
        Assert.Equal(SourceFormat.Xlsx, WorkbookFormat.FromFileName("A.XLSX"));
        Assert.Equal(SourceFormat.Xls, WorkbookFormat.FromFileName("a.xls"));
        Assert.Null(WorkbookFormat.FromFileName("a.csv"));
        Assert.Equal(SourceFormat.Xlsx, WorkbookFormat.Sniff(new MemoryStream(Workbooks.Xlsx(Sheet(Trf, TrfRow)))));
        Assert.Equal(SourceFormat.Xls, WorkbookFormat.Sniff(new MemoryStream(Workbooks.Xls(Sheet(Trf, TrfRow)))));
        Assert.Null(WorkbookFormat.Sniff(new MemoryStream("a,b"u8.ToArray())));
    }
}
