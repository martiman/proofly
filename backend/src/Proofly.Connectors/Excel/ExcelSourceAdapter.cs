using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Excel;

/// <summary>
/// Workbook adapter for one source, for <see cref="SourceFormat.Xlsx"/> (.xlsx) or <see cref="SourceFormat.Xls"/> (legacy
/// .xls). It streams rows with ExcelDataReader, converts cells to text per target column type
/// (<see cref="ExcelCellConverter"/>) and hands each row to the schema's RowMapper; it holds no field semantics.
/// Rows: one worksheet (first, or <see cref="ExcelSourceOptions.SheetName"/>), header at <see cref="ExcelSourceOptions.HeaderRow"/>,
/// fully blank rows skipped (not counted), row numbers = 1-based data rows. A cell that cannot be represented exactly
/// (float precision, boolean, error, numeric in a text column, time zone missing) rejects its row INVALID_VALUE. A formula cell
/// yields its cached value; an absent cached value reads as blank (so a required column is rejected). Merged cells and
/// hidden sheets/rows are not special-cased: a merged range holds its value only in the top-left cell, hidden content is read.
/// Corrupt, password-protected or wrong-format files throw <see cref="SourceFormatException"/>.
/// </summary>
public sealed class ExcelSourceAdapter<TRecord> : ISourceAdapter<TRecord> where TRecord : class, ISourceRecord
{
    private static readonly string Poison = new('\u0001', 100_000);

    static ExcelSourceAdapter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly ISourceSchema<TRecord> _schema;
    private readonly ExcelSourceOptions _options;

    public ExcelSourceAdapter(ISourceSchema<TRecord> schema, SourceFormat format, ExcelSourceOptions? options = null)
    {
        if (format is not (SourceFormat.Xlsx or SourceFormat.Xls)) throw new ArgumentException("Format must be Xlsx or Xls.", nameof(format));
        _schema = schema;
        Format = format;
        _options = options ?? new ExcelSourceOptions();
        _options.Validate();
    }

    public string SourceName => _schema.SourceName;
    public SourceFormat Format { get; }
    public IReadOnlyCollection<string> SupportedSchemaVersions => _schema.SupportedVersions;

    private IExcelDataReader Open(Stream content, out Stream owned)
    {
        owned = content;
        try
        {
            if (!content.CanSeek)
            {
                var ms = new MemoryStream();
                content.CopyTo(ms);
                ms.Position = 0;
                owned = ms;
            }
            var sniffed = WorkbookFormat.Sniff(owned);
            if (sniffed != Format)
                throw new SourceFormatException($"File is not a {(Format == SourceFormat.Xlsx ? ".xlsx" : ".xls")} workbook (content is {(sniffed?.ToString() ?? "unrecognised")}); it may be corrupt or password-protected.");
            var reader = ExcelReaderFactory.CreateReader(owned, new ExcelReaderConfiguration { LeaveOpen = true, FallbackEncoding = Encoding.GetEncoding(1252) });
            SelectSheet(reader);
            return reader;
        }
        catch (SourceFormatException) { throw; }
        catch (InvalidPasswordException e) { throw new SourceFormatException("Workbook is password protected.", e); }
        catch (Exception e) when (e is ExcelReaderException or InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException or FormatException)
        {
            throw new SourceFormatException("Workbook is corrupt or cannot be read: " + e.Message, e);
        }
    }

    private void SelectSheet(IExcelDataReader reader)
    {
        if (_options.SheetName is null) return;
        do
        {
            if (string.Equals(reader.Name, _options.SheetName, StringComparison.Ordinal)) return;
        } while (reader.NextResult());
        throw new SourceFormatException($"Workbook has no worksheet named '{_options.SheetName}'.");
    }

    /// <summary>Reads rows until the header row; returns the names (trailing blank header cells dropped) or null if there is none.</summary>
    private List<string>? ReadHeader(IExcelDataReader r)
    {
        for (var i = 1; i <= _options.HeaderRow; i++)
        {
            if (!SafeRead(r)) return null;
        }
        var names = new List<string>();
        for (var c = 0; c < r.FieldCount; c++)
        {
            var v = r.GetValue(c);
            switch (v)
            {
                case null: names.Add(""); break;
                case string s: names.Add(s); break;
                default: throw new SourceFormatException($"Header cell {c + 1} is not text.");
            }
        }
        while (names.Count > 0 && string.IsNullOrWhiteSpace(names[^1])) names.RemoveAt(names.Count - 1);
        return names.Count == 0 ? null : names;
    }

    private static bool SafeRead(IExcelDataReader r)
    {
        try { return r.Read(); }
        catch (Exception e) when (e is ExcelReaderException or InvalidDataException or IOException or IndexOutOfRangeException or InvalidOperationException or FormatException)
        {
            throw new SourceFormatException("Workbook is corrupt or cannot be read: " + e.Message, e);
        }
    }

    public Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default)
    {
        using var reader = Open(content, out _);
        var header = ReadHeader(reader);
        return Task.FromResult(header is null
            ? SchemaValidation.Fail($"Worksheet has no header row for {SourceName}.")
            : _schema.ValidateHeader(header, descriptor.SchemaVersion));
    }

    private ColumnSpec? Spec(IReadOnlyList<ColumnSpec> columns, string name)
    {
        var c = columns.FirstOrDefault(x => x.Name == name);
        if (c is not null) return c;
        // open-ended optional amount columns (manifest declared_total_<ccy>)
        return _schema.DynamicColumnPrefix is { } p && name.StartsWith(p, StringComparison.Ordinal) ? ColumnSpec.Amount(name, false) : null;
    }

    public async IAsyncEnumerable<ParseResult<TRecord>> ParseAsync(Stream content, SourceDescriptor descriptor, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        using var reader = Open(content, out _);
        var header = ReadHeader(reader) ?? throw new SourceFormatException("Worksheet has no header row.");
        var v = _schema.ValidateHeader(header, descriptor.SchemaVersion);
        if (!v.IsValid) throw new SourceFormatException(v.Message ?? "Header does not match the schema.");
        var columns = _schema.ColumnsFor(descriptor.SchemaVersion);
        var specs = header.Select(h => Spec(columns, h)).ToArray();
        var idIndex = _schema.RowIdColumn is { } idc ? header.IndexOf(idc) : -1;

        var row = 0;
        while (SafeRead(reader))
        {
            ct.ThrowIfCancellationRequested();
            var raw = new object?[Math.Max(reader.FieldCount, header.Count)];
            var blank = true;
            for (var c = 0; c < reader.FieldCount; c++)
            {
                raw[c] = reader.GetValue(c) ?? (reader.GetCellError(c) is { } err ? new ExcelCellError(err.ToString()) : null);
                if (raw[c] is not null and not "" && !(raw[c] is string s0 && string.IsNullOrWhiteSpace(s0))) blank = false;
            }
            if (blank) continue;
            row++;
            yield return MapRow(descriptor, header, specs, raw, row, idIndex);
        }
    }

    private ParseResult<TRecord> MapRow(SourceDescriptor d, List<string> header, ColumnSpec?[] specs, object?[] raw, int row, int idIndex)
    {
        var excerpt = string.Join(",", raw.Select(c => System.Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture)));
        var cells = new Dictionary<string, string?>(header.Count, StringComparer.Ordinal);
        (string Field, string Message)? firstError = null;
        for (var i = 0; i < header.Count; i++)
        {
            var (text, error) = ExcelCellConverter.Convert(raw[i], specs[i]!.Type, _options);
            if (error is not null)
            {
                firstError ??= (header[i], error);
                text = Poison;
            }
            cells[header[i]] = text;
        }
        if (firstError is null)
        {
            for (var i = header.Count; i < raw.Length; i++)
            {
                if (raw[i] is not null and not "" && !(raw[i] is string s && string.IsNullOrWhiteSpace(s)))
                {
                    firstError = ($"column {i + 1}", "cell outside the header range has a value");
                    break;
                }
            }
        }
        var mapped = _schema.Mapper.Map(d.SchemaVersion, row, cells, excerpt);
        if (firstError is not { } fe) return mapped;
        var id = idIndex >= 0 && cells[header[idIndex]] is { } t && !ReferenceEquals(t, Poison) && !string.IsNullOrWhiteSpace(t) ? t : null;
        return ParseResult<TRecord>.Reject(new RowRejection(ReasonCodes.InvalidValue, $"{fe.Field}: {fe.Message}", row, excerpt, id, fe.Field, mapped.Rejection?.ControlAmount));
    }
}
