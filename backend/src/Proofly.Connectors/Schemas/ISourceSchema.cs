using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Mapping;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Schemas;

/// <summary>
/// Format-independent description of one source: its versioned columns, header validation and (through the
/// typed <see cref="ISourceSchema{TRecord}.Mapper"/>) the only implementation of field semantics. CSV, Excel and XML
/// adapters convert their physical data into <c>IReadOnlyDictionary&lt;string,string?&gt;</c> cells and call the mapper.
/// </summary>
public interface ISourceSchema
{
    /// <summary>Manifest source_name (see <see cref="SourceNames"/>).</summary>
    string SourceName { get; }
    Type RecordType { get; }
    IReadOnlyCollection<string> SupportedVersions { get; }
    /// <summary>Column specs of a schema version; throws <see cref="ArgumentException"/> for an unsupported version.</summary>
    IReadOnlyList<ColumnSpec> ColumnsFor(string version);
    /// <summary>Column name prefix of open-ended optional columns (manifest <c>declared_total_&lt;ccy&gt;</c>); null when none.</summary>
    string? DynamicColumnPrefix { get; }
    /// <summary>Column whose value identifies a row in rejections (source_row_id, or the natural id); null if none.</summary>
    string? RowIdColumn { get; }
    /// <summary>
    /// Checks the header names against the version: unsupported version, duplicate, unknown and missing REQUIRED columns
    /// fail (all problems listed in the message). Optional columns may be absent. Names are exact: no trimming, case-sensitive.
    /// </summary>
    SchemaValidation ValidateHeader(IEnumerable<string> headers, string version);
}

public interface ISourceSchema<TRecord> : ISourceSchema where TRecord : class, ISourceRecord
{
    RowMapper<TRecord> Mapper { get; }
}
