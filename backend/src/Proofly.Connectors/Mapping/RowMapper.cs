using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Mapping;

/// <summary>
/// THE place where field semantics live. Takes one row as a name -> cell text dictionary (a null/absent/blank cell is
/// "missing") and returns the canonical record or a rejection. Used unchanged by the CSV, Excel and XML adapters.
/// Order of checks (deterministic, first failure wins): unknown cell names (INVALID_VALUE) -> required cells blank, in
/// column order (REQUIRED_FIELD_MISSING) -> value validity per column, in column order (INVALID_VALUE: type, length,
/// allowed values, ISO 4217, negative where not allowed) -> cross-field rules of the source (e.g. settlement net,
/// CONTROL_VALUE_MISMATCH). Never throws for bad data; never repairs.
/// </summary>
public sealed class RowMapper<TRecord> where TRecord : class, ISourceRecord
{
    private readonly SourceSchema<TRecord> _schema;
    internal RowMapper(SourceSchema<TRecord> schema) => _schema = schema;

    /// <param name="version">Schema version of the delivery.</param>
    /// <param name="rowNumber">1-based data row number (header excluded).</param>
    /// <param name="rawExcerpt">Original row text for the rejection, if the adapter has it.</param>
    public ParseResult<TRecord> Map(string version, int rowNumber, IReadOnlyDictionary<string, string?> cells, string? rawExcerpt = null)
    {
        var columns = _schema.ColumnsFor(version);
        string? rowId = _schema.RowIdColumn is { } idc && cells.TryGetValue(idc, out var idv) && !CellParser.IsBlank(idv) ? idv : null;
        ParseResult<TRecord> Reject(string code, string message, string? field) =>
            ParseResult<TRecord>.Reject(new RowRejection(code, message, rowNumber, rawExcerpt, rowId, field, ControlAmount(columns, cells)));

        var known = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var prefix = _schema.DynamicColumnPrefix;
        foreach (var key in cells.Keys)
        {
            if (!known.Contains(key) && !(prefix is not null && key.StartsWith(prefix, StringComparison.Ordinal)))
                return Reject(ReasonCodes.InvalidValue, $"unknown field '{CellParser.Excerpt(key)}'", key);
        }
        foreach (var c in columns)
        {
            if (c.Required && (!cells.TryGetValue(c.Name, out var v) || CellParser.IsBlank(v)))
                return Reject(ReasonCodes.RequiredFieldMissing, $"{c.Name} is empty", c.Name);
        }
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var c in columns)
        {
            if (!cells.TryGetValue(c.Name, out var text) || CellParser.IsBlank(text)) continue;
            var (value, error) = CellParser.Parse(c, text!);
            if (error is not null) return Reject(ReasonCodes.InvalidValue, $"{c.Name} {error}", c.Name);
            values[c.Name] = value;
        }
        try
        {
            return ParseResult<TRecord>.Accept(rowNumber, _schema.Build(new RowValues(values, columns, cells, prefix)));
        }
        catch (RowRejectException e)
        {
            return Reject(e.ReasonCode, e.Message, e.Field);
        }
    }

    /// <summary>The control amount, when the control amount column and its currency are each valid (nothing else is checked).</summary>
    private Money? ControlAmount(IReadOnlyList<ColumnSpec> columns, IReadOnlyDictionary<string, string?> cells)
    {
        if (_schema.ControlAmountColumn is not { } name) return null;
        var spec = columns.First(c => c.Name == name);
        if (!cells.TryGetValue(name, out var a) || a is null || a != a.Trim() || !CellParser.TryDecimal(a, out var amount, out _)) return null;
        if (!cells.TryGetValue(spec.CurrencyColumn!, out var ccy) || ccy is null || !CellParser.IsCurrency(ccy)) return null;
        return new Money(amount, ccy);
    }
}
