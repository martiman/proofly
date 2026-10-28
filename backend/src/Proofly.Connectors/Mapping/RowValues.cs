using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Mapping;

/// <summary>Thrown by <see cref="RowValues.Reject"/>; caught by the mapper and turned into a RowRejection.</summary>
public sealed class RowRejectException(string reasonCode, string field, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
    public string Field { get; } = field;
}

/// <summary>Values of one row that already passed the column specs; used by record builders. Required columns are never null.</summary>
public sealed class RowValues
{
    private readonly Dictionary<string, object?> _values;
    private readonly Dictionary<string, ColumnSpec> _specs;
    private readonly IReadOnlyDictionary<string, string?> _cells;
    private readonly string? _prefix;

    internal RowValues(Dictionary<string, object?> values, IReadOnlyList<ColumnSpec> specs, IReadOnlyDictionary<string, string?> cells, string? prefix)
    {
        _values = values; _specs = specs.ToDictionary(s => s.Name, StringComparer.Ordinal); _cells = cells; _prefix = prefix;
    }

    public string Str(string name) => (string)_values[name]!;
    public string? OptStr(string name) => _values.GetValueOrDefault(name) as string;
    public DateOnly Date(string name) => (DateOnly)_values[name]!;
    public DateOnly? OptDate(string name) => _values.GetValueOrDefault(name) as DateOnly?;
    public DateTimeOffset Ts(string name) => (DateTimeOffset)_values[name]!;
    public DateTimeOffset? OptTs(string name) => _values.GetValueOrDefault(name) as DateTimeOffset?;
    public int? OptInt(string name) => _values.GetValueOrDefault(name) as int?;
    public Money Money(string name) => new((decimal)_values[name]!, (string)_values[_specs[name].CurrencyColumn!]!);
    public Money? OptMoney(string name) => _values.GetValueOrDefault(name) is decimal d ? new Money(d, (string)_values[_specs[name].CurrencyColumn!]!) : null;
    public T Enum<T>(string name) where T : struct, Enum =>
        WireEnum.TryParse<T>((string)_values[name]!, out var v) ? v : throw new InvalidOperationException($"{name} was not validated as {typeof(T).Name}.");

    /// <summary>Non-blank cells of the open-ended prefix columns (manifest declared_total_&lt;ccy&gt;), verbatim.</summary>
    public IReadOnlyDictionary<string, string> PrefixCells() =>
        _prefix is null ? new Dictionary<string, string>()
            : _cells.Where(c => c.Key.StartsWith(_prefix, StringComparison.Ordinal) && !CellParser.IsBlank(c.Value)).ToDictionary(c => c.Key, c => c.Value!);

    /// <summary>Rejects the row (cross-field rule), e.g. CONTROL_VALUE_MISMATCH.</summary>
    public static Exception Reject(string reasonCode, string field, string message) => new RowRejectException(reasonCode, field, message);
}
