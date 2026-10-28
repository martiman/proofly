using System.Runtime.CompilerServices;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Abstractions;

/// <summary>Physical formats an adapter can read. Xlsx = Office Open XML workbook (.xlsx), Xls = legacy BIFF8 workbook (.xls).</summary>
public enum SourceFormat { Csv, Xlsx, Xml, Xls }

/// <summary>Stable reason codes (docs/controls/data-completeness.md) used by adapters and the ingestion framework.</summary>
public static class ReasonCodes
{
    public const string RequiredFieldMissing = "REQUIRED_FIELD_MISSING";
    public const string InvalidValue = "INVALID_VALUE";
    public const string DuplicateRecord = "DUPLICATE_RECORD";
    public const string DuplicateDelivery = "DUPLICATE_DELIVERY";
    public const string SchemaUnsupported = "SCHEMA_UNSUPPORTED";
    public const string SequenceGap = "SEQUENCE_GAP";
    public const string ControlCountMismatch = "CONTROL_COUNT_MISMATCH";
    public const string ControlValueMismatch = "CONTROL_VALUE_MISMATCH";
    public const string CorrectionLinkMissing = "CORRECTION_LINK_MISSING";
    public const string SourceMissing = "SOURCE_MISSING";
    public const string FileUnreadable = "FILE_UNREADABLE";
    public const string EvidenceMissing = "EVIDENCE_MISSING";
}

/// <summary>What the adapter needs to know about the delivery it is reading (taken from the manifest).</summary>
public sealed record SourceDescriptor(
    string SourceName, SourceFormat Format, string SchemaVersion, string DeliveryId, DateOnly BusinessDate,
    string FileName, string? LegalEntityId = null);

/// <summary>
/// A source row the adapter refused. Adapters never repair data: a row that fails the documented contract is
/// rejected with a stable reason code and stays visible in the completeness denominator.
/// </summary>
/// <param name="ReasonCode">One of <see cref="ReasonCodes"/> (REQUIRED_FIELD_MISSING, INVALID_VALUE, CONTROL_VALUE_MISMATCH, ...).</param>
/// <param name="Message">Human readable reason, e.g. "client_account_id is empty".</param>
/// <param name="RowNumber">1-based data row number (header excluded); for xlsx/xml the ordinal of the row/element.</param>
/// <param name="RawExcerpt">Original row text as received (truncated by the framework to 500 characters); null if unavailable.</param>
/// <param name="SourceRowId">The row's source_row_id when it could be read.</param>
/// <param name="Field">The offending column/field when applicable.</param>
/// <param name="ControlAmount">
/// The row's control amount when amount and currency were themselves valid. It lets the framework compare declared
/// control totals over ALL rows (the manifest counts rejected rows too). Null if the amount is not usable.
/// </param>
public sealed record RowRejection(
    string ReasonCode, string Message, int RowNumber, string? RawExcerpt = null, string? SourceRowId = null,
    string? Field = null, Money? ControlAmount = null);

/// <summary>Outcome for exactly one source row: an accepted record or a rejection.</summary>
public sealed record ParseResult<TRecord>(int RowNumber, TRecord? Record, RowRejection? Rejection) where TRecord : class
{
    public bool IsAccepted => Record is not null;
    public static ParseResult<TRecord> Accept(int rowNumber, TRecord record) => new(rowNumber, record, null);
    public static ParseResult<TRecord> Reject(RowRejection rejection) => new(rejection.RowNumber, null, rejection);
}

/// <summary>Result of the header/schema check, performed before any row is read.</summary>
public sealed record SchemaValidation(bool IsValid, string? ReasonCode = null, string? Message = null)
{
    public static SchemaValidation Ok { get; } = new(true);
    public static SchemaValidation Fail(string message, string reasonCode = ReasonCodes.SchemaUnsupported) => new(false, reasonCode, message);
}

/// <summary>Throw from an adapter when the file as a whole cannot be read (not UTF-8, corrupt xlsx, malformed XML).</summary>
public sealed class SourceFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Non-generic view used by the registry and the ingestion framework.</summary>
public interface ISourceAdapter
{
    /// <summary>Manifest source_name this adapter reads (see <see cref="SourceNames"/>).</summary>
    string SourceName { get; }
    SourceFormat Format { get; }
    /// <summary>Schema versions the adapter understands; others are rejected as SCHEMA_UNSUPPORTED at registration.</summary>
    IReadOnlyCollection<string> SupportedSchemaVersions { get; }
    Type RecordType { get; }

    /// <summary>Checks headers/structure against the documented contract. Reads the stream from its start.</summary>
    Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default);

    /// <summary>Same as the typed ParseAsync, with records widened to <see cref="ISourceRecord"/>.</summary>
    IAsyncEnumerable<ParseResult<ISourceRecord>> ParseRecordsAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default);
}

/// <summary>
/// The adapter contract. Implement <see cref="ISourceAdapter.SourceName"/>, <see cref="ISourceAdapter.Format"/>,
/// <see cref="ISourceAdapter.SupportedSchemaVersions"/>, <see cref="ISourceAdapter.ValidateSchemaAsync"/> and
/// <see cref="ParseAsync"/>. Rules: yield exactly one <see cref="ParseResult{TRecord}"/> per data row, in file order;
/// stream rows rather than buffering the file; never throw for a bad row (reject it); throw
/// <see cref="SourceFormatException"/> only when the whole file is unreadable; no database or clock access;
/// cross-row rules (duplicates), reference checks and control totals are the framework's job, not the adapter's.
/// </summary>
public interface ISourceAdapter<TRecord> : ISourceAdapter where TRecord : class, ISourceRecord
{
    Type ISourceAdapter.RecordType => typeof(TRecord);

    IAsyncEnumerable<ParseResult<TRecord>> ParseAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default);

    async IAsyncEnumerable<ParseResult<ISourceRecord>> ISourceAdapter.ParseRecordsAsync(
        Stream content, SourceDescriptor descriptor, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var r in ParseAsync(content, descriptor, ct).WithCancellation(ct))
        {
            yield return new ParseResult<ISourceRecord>(r.RowNumber, r.Record, r.Rejection);
        }
    }
}

/// <summary>Resolves adapters by manifest source name and file format (case-insensitive source name).</summary>
public interface ISourceAdapterRegistry
{
    /// <summary>Null when none is registered.</summary>
    ISourceAdapter? Find(string sourceName, SourceFormat format);
    /// <summary>Throws <see cref="KeyNotFoundException"/> when none is registered.</summary>
    ISourceAdapter Resolve(string sourceName, SourceFormat format);
    ISourceAdapter<TRecord> Resolve<TRecord>(string sourceName, SourceFormat format) where TRecord : class, ISourceRecord;
    IReadOnlyCollection<ISourceAdapter> Adapters { get; }
}

/// <summary>Default registry. Build it from the adapters found in DI; a duplicate (source, format) is a configuration error.</summary>
public sealed class SourceAdapterRegistry : ISourceAdapterRegistry
{
    private readonly Dictionary<(string, SourceFormat), ISourceAdapter> _map = [];

    public SourceAdapterRegistry(IEnumerable<ISourceAdapter> adapters)
    {
        foreach (var a in adapters)
        {
            if (!_map.TryAdd((a.SourceName.ToLowerInvariant(), a.Format), a))
            {
                throw new InvalidOperationException($"Two adapters registered for source '{a.SourceName}' and format {a.Format}.");
            }
        }
    }

    public IReadOnlyCollection<ISourceAdapter> Adapters => _map.Values;

    public ISourceAdapter? Find(string sourceName, SourceFormat format) =>
        _map.GetValueOrDefault((sourceName.ToLowerInvariant(), format));

    public ISourceAdapter Resolve(string sourceName, SourceFormat format) =>
        Find(sourceName, format) ?? throw new KeyNotFoundException($"No adapter for source '{sourceName}' and format {format}.");

    public ISourceAdapter<TRecord> Resolve<TRecord>(string sourceName, SourceFormat format) where TRecord : class, ISourceRecord =>
        Resolve(sourceName, format) as ISourceAdapter<TRecord>
        ?? throw new InvalidCastException($"Adapter for '{sourceName}' does not produce {typeof(TRecord).Name}.");
}
