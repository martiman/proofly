using System.Runtime.CompilerServices;
using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Csv;

/// <summary>
/// CSV adapter for one source: decodes, splits records and hands each row to the schema's <c>RowMapper</c> as a
/// header-name -> cell dictionary. It holds no field semantics. Blank lines are skipped (not counted); a row with the
/// wrong number of cells or malformed quoting is rejected with INVALID_VALUE (never crashes, never realigned); an
/// undecodable or unterminated file throws <see cref="SourceFormatException"/>. Row numbers are 1-based data rows.
/// </summary>
public sealed class CsvSourceAdapter<TRecord> : ISourceAdapter<TRecord> where TRecord : class, ISourceRecord
{
    private readonly ISourceSchema<TRecord> _schema;
    private readonly CsvSourceOptions _options;

    public CsvSourceAdapter(ISourceSchema<TRecord> schema, CsvSourceOptions? options = null)
    {
        _schema = schema;
        _options = options ?? new CsvSourceOptions();
        _options.Validate();
    }

    public string SourceName => _schema.SourceName;
    public SourceFormat Format => SourceFormat.Csv;
    public IReadOnlyCollection<string> SupportedSchemaVersions => _schema.SupportedVersions;

    private CsvRecordReader Open(Stream content) =>
        new(new StreamReader(content, _options.ResolveEncoding(), detectEncodingFromByteOrderMarks: false, bufferSize: 16384, leaveOpen: true), _options.Delimiter);

    private static async ValueTask<CsvRecord?> ReadSafe(CsvRecordReader r, CancellationToken ct)
    {
        try { return await r.ReadAsync(ct); }
        catch (DecoderFallbackException e) { throw new SourceFormatException("File is not valid in the declared encoding.", e); }
    }

    public async Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default)
    {
        var header = await ReadHeaderAsync(Open(content), ct);
        return header is null
            ? SchemaValidation.Fail($"File has no header row for {SourceName}.")
            : _schema.ValidateHeader(header, descriptor.SchemaVersion);
    }

    private static async Task<List<string>?> ReadHeaderAsync(CsvRecordReader r, CancellationToken ct)
    {
        while (await ReadSafe(r, ct) is { } rec)
        {
            if (rec.IsBlank) continue;
            return rec.Problem is null ? rec.Fields.ToList() : throw new SourceFormatException($"Malformed header row: {rec.Problem}.");
        }
        return null;
    }

    public async IAsyncEnumerable<ParseResult<TRecord>> ParseAsync(Stream content, SourceDescriptor descriptor, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reader = Open(content);
        var header = await ReadHeaderAsync(reader, ct) ?? throw new SourceFormatException("File has no header row.");
        var v = _schema.ValidateHeader(header, descriptor.SchemaVersion);
        if (!v.IsValid) throw new SourceFormatException(v.Message ?? "Header does not match the schema.");
        var idIndex = _schema.RowIdColumn is { } idc ? header.IndexOf(idc) : -1;

        var row = 0;
        while (await ReadSafe(reader, ct) is { } rec)
        {
            if (rec.IsBlank) continue;
            row++;
            if (rec.Problem is not null || rec.Fields.Count != header.Count)
            {
                var msg = rec.Problem is not null ? $"malformed CSV row: {rec.Problem}" : $"row has {rec.Fields.Count} cells, header has {header.Count}";
                var id = idIndex >= 0 && idIndex < rec.Fields.Count && !string.IsNullOrWhiteSpace(rec.Fields[idIndex]) ? rec.Fields[idIndex] : null;
                yield return ParseResult<TRecord>.Reject(new RowRejection(ReasonCodes.InvalidValue, msg, row, rec.Raw, id));
                continue;
            }
            var cells = new Dictionary<string, string?>(header.Count, StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++) cells[header[i]] = rec.Fields[i];
            yield return _schema.Mapper.Map(descriptor.SchemaVersion, row, cells, rec.Raw);
        }
    }
}
