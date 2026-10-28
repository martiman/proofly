using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Xml;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Excel;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests.Excel;

/// <summary>A numeric cell written with this exact text (to store values a double literal cannot express).</summary>
internal sealed record RawNumber(string Text);
/// <summary>A formula cell with its cached value (null = no cached value stored).</summary>
internal sealed record Formula(string Text, object? Cached);
internal sealed record ErrorCell(string Code);
internal sealed record SheetData(string Name, IReadOnlyList<object?[]?> Rows, bool Hidden = false);

/// <summary>
/// Minimal in-test writers. XLSX: a hand-written Open XML package (System.IO.Compression, no third-party writer).
/// XLS: a hand-built BIFF8 workbook stream wrapped in a minimal OLE2 compound file (see <see cref="Biff"/>).
/// Cell values: string, double/decimal/int, DateTime (date style), bool, <see cref="RawNumber"/>, <see cref="Formula"/>,
/// <see cref="ErrorCell"/>, null (empty). A null row leaves a gap (fully blank row).
/// </summary>
internal static class Workbooks
{
    public static byte[] Xlsx(params SheetData[] sheets)
    {
        var ms = new MemoryStream();
        WriteXlsx(ms, sheets);
        return ms.ToArray();
    }

    public static void WriteXlsx(Stream output, params SheetData[] sheets)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Entry(string name, Action<XmlWriter> body)
        {
            using var s = zip.CreateEntry(name).Open();
            using var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            w.WriteStartDocument(true);
            body(w);
        }
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Entry("[Content_Types].xml", w =>
        {
            w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            w.WriteStartElement("Default"); w.WriteAttributeString("Extension", "rels"); w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); w.WriteEndElement();
            w.WriteStartElement("Default"); w.WriteAttributeString("Extension", "xml"); w.WriteAttributeString("ContentType", "application/xml"); w.WriteEndElement();
            void Ov(string part, string type) { w.WriteStartElement("Override"); w.WriteAttributeString("PartName", part); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }
            Ov("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Ov("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (var i = 0; i < sheets.Length; i++) Ov($"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            w.WriteEndElement();
        });
        Entry("_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            w.WriteStartElement("Relationship"); w.WriteAttributeString("Id", "rId1"); w.WriteAttributeString("Type", rel + "/officeDocument"); w.WriteAttributeString("Target", "xl/workbook.xml"); w.WriteEndElement();
            w.WriteEndElement();
        });
        Entry("xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", main);
            w.WriteAttributeString("xmlns", "r", null, rel);
            w.WriteStartElement("sheets");
            for (var i = 0; i < sheets.Length; i++)
            {
                w.WriteStartElement("sheet"); w.WriteAttributeString("name", sheets[i].Name); w.WriteAttributeString("sheetId", (i + 1).ToString());
                if (sheets[i].Hidden) w.WriteAttributeString("state", "hidden");
                w.WriteAttributeString("id", rel, "rId" + (i + 1)); w.WriteEndElement();
            }
            w.WriteEndElement(); w.WriteEndElement();
        });
        Entry("xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (var i = 0; i < sheets.Length; i++)
            {
                w.WriteStartElement("Relationship"); w.WriteAttributeString("Id", "rId" + (i + 1)); w.WriteAttributeString("Type", rel + "/worksheet"); w.WriteAttributeString("Target", $"worksheets/sheet{i + 1}.xml"); w.WriteEndElement();
            }
            w.WriteStartElement("Relationship"); w.WriteAttributeString("Id", "rIdS"); w.WriteAttributeString("Type", rel + "/styles"); w.WriteAttributeString("Target", "styles.xml"); w.WriteEndElement();
            w.WriteEndElement();
        });
        Entry("xl/styles.xml", w =>
        {
            w.WriteStartElement("styleSheet", main);
            w.WriteStartElement("fonts"); w.WriteAttributeString("count", "1"); w.WriteStartElement("font"); w.WriteStartElement("sz"); w.WriteAttributeString("val", "11"); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("fills"); w.WriteAttributeString("count", "1"); w.WriteStartElement("fill"); w.WriteStartElement("patternFill"); w.WriteAttributeString("patternType", "none"); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("borders"); w.WriteAttributeString("count", "1"); w.WriteStartElement("border"); w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("cellStyleXfs"); w.WriteAttributeString("count", "1"); w.WriteStartElement("xf"); w.WriteEndElement(); w.WriteEndElement();
            w.WriteStartElement("cellXfs"); w.WriteAttributeString("count", "3");
            w.WriteStartElement("xf"); w.WriteAttributeString("numFmtId", "0"); w.WriteEndElement();
            w.WriteStartElement("xf"); w.WriteAttributeString("numFmtId", "14"); w.WriteAttributeString("applyNumberFormat", "1"); w.WriteEndElement();   // date
            w.WriteStartElement("xf"); w.WriteAttributeString("numFmtId", "22"); w.WriteAttributeString("applyNumberFormat", "1"); w.WriteEndElement();   // date-time
            w.WriteEndElement(); w.WriteEndElement();
        });
        for (var i = 0; i < sheets.Length; i++)
        {
            var sheet = sheets[i];
            Entry($"xl/worksheets/sheet{i + 1}.xml", w =>
            {
                w.WriteStartElement("worksheet", main);
                w.WriteStartElement("sheetData");
                for (var r = 0; r < sheet.Rows.Count; r++)
                {
                    if (sheet.Rows[r] is not { } cells) continue;
                    w.WriteStartElement("row"); w.WriteAttributeString("r", (r + 1).ToString());
                    for (var c = 0; c < cells.Length; c++) WriteCell(w, cells[c], r + 1, c);
                    w.WriteEndElement();
                }
                w.WriteEndElement(); w.WriteEndElement();
            });
        }
    }

    private static string Col(int c) => c < 26 ? ((char)('A' + c)).ToString() : Col(c / 26 - 1) + (char)('A' + c % 26);

    private static string Num(object v) => v switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        RawNumber rn => rn.Text,
        _ => throw new ArgumentException("not numeric"),
    };

    private static double Serial(DateTime d) => (d - new DateTime(1899, 12, 30)).TotalDays;

    private static void WriteCell(XmlWriter w, object? v, int row, int col)
    {
        if (v is null) return;
        w.WriteStartElement("c"); w.WriteAttributeString("r", Col(col) + row);
        switch (v)
        {
            case string s:
                w.WriteAttributeString("t", "inlineStr"); w.WriteStartElement("is"); w.WriteStartElement("t"); w.WriteAttributeString("xml", "space", null, "preserve"); w.WriteString(s); w.WriteEndElement(); w.WriteEndElement(); break;
            case bool b:
                w.WriteAttributeString("t", "b"); w.WriteElementString("v", b ? "1" : "0"); break;
            case DateTime dt:
                w.WriteAttributeString("s", dt.TimeOfDay == TimeSpan.Zero ? "1" : "2"); w.WriteElementString("v", Serial(dt).ToString("R", CultureInfo.InvariantCulture)); break;
            case ErrorCell e:
                w.WriteAttributeString("t", "e"); w.WriteElementString("v", e.Code); break;
            case Formula f:
                if (f.Cached is string fs) w.WriteAttributeString("t", "str");
                w.WriteElementString("f", f.Text);
                if (f.Cached is not null) w.WriteElementString("v", f.Cached is string cs ? cs : Num(f.Cached));
                break;
            default:
                w.WriteElementString("v", Num(v)); break;
        }
        w.WriteEndElement();
    }

    public static byte[] Xls(params SheetData[] sheets) => Biff.Workbook(sheets, password: false);
    public static byte[] XlsPasswordProtected() => Biff.Workbook([new SheetData("s", [["a"]])], password: true);
}

/// <summary>
/// Hand-built BIFF8 (.xls). Workbook stream = globals (BOF, CODEPAGE, DATE1904, FONT, 17 XF records where XF 16 has the
/// built-in date format 14, BOUNDSHEET per sheet, EOF) + one sheet substream per sheet (BOF, DIMENSIONS, LABEL/NUMBER/BOOLERR cells, EOF),
/// zero-padded to 4096 bytes so it lives in regular sectors, inside a version 3 OLE2 container with one FAT sector and
/// a directory of Root Entry + "Workbook". Strings are written as uncompressed-flag-0 8-bit LABEL records (ASCII only). No SST/ROW records.
/// </summary>
internal static class Biff
{
    public static byte[] Workbook(SheetData[] sheets, bool password)
    {
        var sheetStreams = sheets.Select(Sheet).ToArray();
        var globalsNoSheets = Globals(sheets, [], password);
        var offsets = new int[sheets.Length];
        var pos = globalsNoSheets.Length;
        for (var i = 0; i < sheets.Length; i++) { offsets[i] = pos; pos += sheetStreams[i].Length; }
        var all = new List<byte>(Globals(sheets, offsets, password));
        foreach (var s in sheetStreams) all.AddRange(s);
        return Ole(all.ToArray());
    }

    private static byte[] Rec(ushort id, params byte[][] parts)
    {
        var data = parts.SelectMany(p => p).ToArray();
        return [.. BitConverter.GetBytes(id), .. BitConverter.GetBytes((ushort)data.Length), .. data];
    }

    private static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
    private static byte[] U32(long v) => BitConverter.GetBytes((uint)v);

    private static byte[] Bof(ushort type) => Rec(0x0809, U16(0x0600), U16(type), U16(0x0DBB), U16(0x07CC), U32(0), U32(6));
    private static byte[] Eof() => Rec(0x000A);

    private static byte[] Globals(SheetData[] sheets, int[] offsets, bool password)
    {
        var l = new List<byte>();
        l.AddRange(Bof(0x0005));
        if (password) l.AddRange(Rec(0x002F, U16(1), U16(1), U16(1)));   // FILEPASS (RC4 encrypted)
        l.AddRange(Rec(0x0042, U16(1252)));                               // CODEPAGE
        l.AddRange(Rec(0x0022, U16(0)));                                  // DATE1904 = 0
        l.AddRange(Rec(0x0031, U16(200), U16(0), U16(0x7FFF), U16(400), U16(0), [0, 0, 0, 0], [5], [0], Encoding.ASCII.GetBytes("Arial"))); // FONT
        for (var i = 0; i < 17; i++)
        {
            // XF: font, format, flags (bit2 style xf for first 16), align, rotation, indent, used attributes, borders/fill (padded)
            var fmt = i == 16 ? 14 : 0;
            var flags = i < 16 ? 0xFFF5 : 0x0001;
            l.AddRange(Rec(0x00E0, U16(0), U16(fmt), U16(flags), [0], [0], [0], [0], U32(0), U32(0), U16(0)));
        }
        for (var i = 0; i < sheets.Length; i++)
        {
            var name = Encoding.ASCII.GetBytes(sheets[i].Name);
            l.AddRange(Rec(0x0085, U32(i < offsets.Length ? offsets[i] : 0), [(byte)(sheets[i].Hidden ? 1 : 0)], [0], [(byte)name.Length], [0], name));
        }
        l.AddRange(Eof());
        return l.ToArray();
    }

    private static byte[] Sheet(SheetData s)
    {
        var l = new List<byte>();
        l.AddRange(Bof(0x0010));
        var cols = s.Rows.Where(r => r is not null).Select(r => r!.Length).DefaultIfEmpty(0).Max();
        l.AddRange(Rec(0x0200, U32(0), U32(s.Rows.Count), U16(0), U16(cols), U16(0)));   // DIMENSIONS
        for (var r = 0; r < s.Rows.Count; r++)
        {
            if (s.Rows[r] is not { } cells) continue;
            for (var c = 0; c < cells.Length; c++)
            {
                switch (cells[c])
                {
                    case null: break;
                    case string str:
                        var b = Encoding.ASCII.GetBytes(str);
                        l.AddRange(Rec(0x0204, U16(r), U16(c), U16(15), U16(b.Length), [0], b)); break;   // LABEL
                    case DateTime dt:
                        l.AddRange(Rec(0x0203, U16(r), U16(c), U16(16), BitConverter.GetBytes((dt - new DateTime(1899, 12, 30)).TotalDays))); break;   // NUMBER with date XF
                    case bool bo:
                        l.AddRange(Rec(0x0205, U16(r), U16(c), U16(15), [(byte)(bo ? 1 : 0)], [0])); break;   // BOOLERR
                    case ErrorCell:
                        l.AddRange(Rec(0x0205, U16(r), U16(c), U16(15), [0x07], [1])); break;   // #DIV/0!
                    case double d:
                        l.AddRange(Rec(0x0203, U16(r), U16(c), U16(15), BitConverter.GetBytes(d))); break;
                    case int n:
                        l.AddRange(Rec(0x0203, U16(r), U16(c), U16(15), BitConverter.GetBytes((double)n))); break;
                    default: throw new NotSupportedException(cells[c]!.GetType().Name);
                }
            }
        }
        l.AddRange(Eof());
        return l.ToArray();
    }

    private static byte[] Ole(byte[] stream)
    {
        const int sector = 512;
        var size = Math.Max(stream.Length, 4096);
        var dataSectors = (size + sector - 1) / sector;
        var padded = new byte[dataSectors * sector];
        stream.CopyTo(padded, 0);

        var header = new byte[sector];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BitConverter.GetBytes((ushort)0x003E).CopyTo(header, 24);
        BitConverter.GetBytes((ushort)3).CopyTo(header, 26);
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(header, 28);
        BitConverter.GetBytes((ushort)9).CopyTo(header, 30);
        BitConverter.GetBytes((ushort)6).CopyTo(header, 32);
        BitConverter.GetBytes(1).CopyTo(header, 44);              // FAT sectors
        BitConverter.GetBytes(1).CopyTo(header, 48);              // first directory sector
        BitConverter.GetBytes(4096).CopyTo(header, 56);           // mini stream cutoff
        BitConverter.GetBytes(-2).CopyTo(header, 60);             // first mini FAT sector
        BitConverter.GetBytes(-2).CopyTo(header, 68);             // first DIFAT sector
        for (var i = 0; i < 109; i++) BitConverter.GetBytes(-1).CopyTo(header, 76 + i * 4);
        BitConverter.GetBytes(0).CopyTo(header, 76);              // DIFAT[0] = sector 0 is the FAT

        var fat = new byte[sector];
        for (var i = 0; i < sector / 4; i++) BitConverter.GetBytes(-1).CopyTo(fat, i * 4);
        BitConverter.GetBytes(-3).CopyTo(fat, 0);                 // sector 0: FAT
        BitConverter.GetBytes(-2).CopyTo(fat, 4);                 // sector 1: directory (end of chain)
        for (var i = 0; i < dataSectors; i++) BitConverter.GetBytes(i == dataSectors - 1 ? -2 : 3 + i).CopyTo(fat, (2 + i) * 4);

        var dir = new byte[sector];
        void Entry(int index, string name, byte type, int child, int start, int len)
        {
            var o = index * 128;
            Encoding.Unicode.GetBytes(name).CopyTo(dir, o);
            BitConverter.GetBytes((ushort)(name.Length * 2 + 2)).CopyTo(dir, o + 64);
            dir[o + 66] = type; dir[o + 67] = 1;
            BitConverter.GetBytes(-1).CopyTo(dir, o + 68);
            BitConverter.GetBytes(-1).CopyTo(dir, o + 72);
            BitConverter.GetBytes(child).CopyTo(dir, o + 76);
            BitConverter.GetBytes(start).CopyTo(dir, o + 116);
            BitConverter.GetBytes(len).CopyTo(dir, o + 120);
        }
        Entry(0, "Root Entry", 5, 1, -2, 0);
        Entry(1, "Workbook", 2, -1, 2, padded.Length);
        for (var i = 2; i < 4; i++) { BitConverter.GetBytes(-1).CopyTo(dir, i * 128 + 68); BitConverter.GetBytes(-1).CopyTo(dir, i * 128 + 72); BitConverter.GetBytes(-1).CopyTo(dir, i * 128 + 76); }

        return [.. header, .. fat, .. dir, .. padded];
    }
}

/// <summary>Row-level helpers: build a header+rows sheet for a source from Samples and parse through the Excel adapters.</summary>
internal static class ExcelSamples
{
    public static object?[] HeaderRow(string source) => Samples.Headers[source].Cast<object?>().ToArray();

    public static object?[] Cells(string source, Dictionary<string, object?> row) => Samples.Headers[source].Select(h => row[h]).ToArray();

    public static Dictionary<string, object?> Typed(string source) => Samples.Row(source).ToDictionary(kv => kv.Key, kv => (object?)(kv.Value.Length == 0 ? null : kv.Value));

    public static Dictionary<string, object?> With(this Dictionary<string, object?> row, string column, object? value) => new(row) { [column] = value };

    public static SheetData Sheet(string source, params Dictionary<string, object?>[] rows) =>
        new("Data", [HeaderRow(source), .. rows.Select(r => Cells(source, r))]);

    public static SourceDescriptor Descriptor(string source, SourceFormat format) =>
        new(source, format, "1.0", "DEL-1", new DateOnly(2026, 10, 27), source + (format == SourceFormat.Xls ? ".xls" : ".xlsx"));

    public static ISourceAdapter Adapter(string source, SourceFormat format, ExcelSourceOptions? o = null) => source switch
    {
        SourceNames.TransferInstructions => new ExcelSourceAdapter<TransferInstruction>(SourceSchemas.TransferInstructions, format, o),
        SourceNames.DirectDebitEvents => new ExcelSourceAdapter<DirectDebitEvent>(SourceSchemas.DirectDebitEvents, format, o),
        SourceNames.InternalLedgerEntries => new ExcelSourceAdapter<LedgerEntry>(SourceSchemas.InternalLedgerEntries, format, o),
        SourceNames.ClientAccountBalances => new ExcelSourceAdapter<AccountBalance>(SourceSchemas.ClientAccountBalances, format, o),
        SourceNames.BankingTransactions => new ExcelSourceAdapter<BankTransaction>(SourceSchemas.BankingTransactions, format, o),
        SourceNames.SafeguardingBalances => new ExcelSourceAdapter<SafeguardingBalance>(SourceSchemas.SafeguardingBalances, format, o),
        SourceNames.CardTransactions => new ExcelSourceAdapter<CardEvent>(SourceSchemas.CardTransactions, format, o),
        SourceNames.CardSettlements => new ExcelSourceAdapter<CardSettlement>(SourceSchemas.CardSettlements, format, o),
        SourceNames.DeliveryManifest => new ExcelSourceAdapter<DeliveryManifestEntry>(SourceSchemas.DeliveryManifest, format, o),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static async Task<List<ParseResult<ISourceRecord>>> Parse(string source, byte[] content, SourceFormat format = SourceFormat.Xlsx, ExcelSourceOptions? o = null)
    {
        var list = new List<ParseResult<ISourceRecord>>();
        await foreach (var r in Adapter(source, format, o).ParseRecordsAsync(new MemoryStream(content), Descriptor(source, format))) list.Add(r);
        return list;
    }

    public static Task<SchemaValidation> Validate(string source, byte[] content, SourceFormat format = SourceFormat.Xlsx, ExcelSourceOptions? o = null) =>
        Adapter(source, format, o).ValidateSchemaAsync(new MemoryStream(content), Descriptor(source, format));
}
