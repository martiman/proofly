using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Mapping;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Schemas;

/// <summary>Declarative schema: columns per version, a record builder over validated values, and the control amount column.</summary>
public sealed class SourceSchema<TRecord> : ISourceSchema<TRecord> where TRecord : class, ISourceRecord
{
    private readonly Dictionary<string, IReadOnlyList<ColumnSpec>> _versions;

    /// <param name="versions">Schema version -> columns (at least one).</param>
    /// <param name="build">Builds the canonical record from values already validated against the column specs; throws via <see cref="RowValues.Reject"/> for cross-field rules.</param>
    /// <param name="controlAmountColumn">Amount column reported as ControlAmount on rejections when amount and currency are valid.</param>
    public SourceSchema(string sourceName, IReadOnlyDictionary<string, IReadOnlyList<ColumnSpec>> versions, string? rowIdColumn,
        string? controlAmountColumn, Func<RowValues, TRecord> build, string? dynamicColumnPrefix = null)
    {
        SourceName = sourceName;
        _versions = new Dictionary<string, IReadOnlyList<ColumnSpec>>(versions, StringComparer.Ordinal);
        RowIdColumn = rowIdColumn;
        ControlAmountColumn = controlAmountColumn;
        Build = build;
        DynamicColumnPrefix = dynamicColumnPrefix;
        Mapper = new RowMapper<TRecord>(this);
    }

    public string SourceName { get; }
    public Type RecordType => typeof(TRecord);
    public IReadOnlyCollection<string> SupportedVersions => _versions.Keys;
    public string? DynamicColumnPrefix { get; }
    public string? RowIdColumn { get; }
    public string? ControlAmountColumn { get; }
    internal Func<RowValues, TRecord> Build { get; }
    public RowMapper<TRecord> Mapper { get; }

    public IReadOnlyList<ColumnSpec> ColumnsFor(string version) =>
        _versions.TryGetValue(version, out var c) ? c : throw new ArgumentException($"Schema version '{version}' is not supported for '{SourceName}'.", nameof(version));

    public SchemaValidation ValidateHeader(IEnumerable<string> headers, string version)
    {
        if (!_versions.TryGetValue(version, out var columns))
            return SchemaValidation.Fail($"Schema version '{version}' is not supported for '{SourceName}' (supported: {string.Join(", ", _versions.Keys)}).");
        var list = headers.ToList();
        var problems = new List<string>();
        var dupes = list.GroupBy(h => h, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) problems.Add($"duplicate columns: {string.Join(", ", dupes.Select(Show))}");
        var known = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = list.Where(h => !known.Contains(h) && !(DynamicColumnPrefix is { } p && h.StartsWith(p, StringComparison.Ordinal))).Distinct().ToList();
        if (unknown.Count > 0) problems.Add($"unknown columns: {string.Join(", ", unknown.Select(Show))}");
        var present = list.ToHashSet(StringComparer.Ordinal);
        var missing = columns.Where(c => c.Required && !present.Contains(c.Name)).Select(c => c.Name).ToList();
        if (missing.Count > 0) problems.Add($"missing required columns: {string.Join(", ", missing)}");
        if (DynamicColumnPrefix == ManifestTotals.Prefix)
        {
            // header-level check of declared_total_<ccy> names (lower-case ISO 4217)
            var bad = list.Where(ManifestTotals.IsTotalColumn)
                .Where(h => h[ManifestTotals.Prefix.Length..] is var code && (code != code.ToLowerInvariant() || !Iso4217.IsValid(code.ToUpperInvariant())))
                .ToList();
            if (bad.Count > 0) problems.Add($"columns with an invalid currency code: {string.Join(", ", bad.Select(Show))}");
        }
        return problems.Count == 0
            ? SchemaValidation.Ok
            : SchemaValidation.Fail($"Header does not match {SourceName} v{version}: {string.Join("; ", problems)}.");
    }

    private static string Show(string s) => s.Length == 0 ? "(empty name)" : $"'{(s.Length > 60 ? s[..60] + "..." : s)}'";
}
