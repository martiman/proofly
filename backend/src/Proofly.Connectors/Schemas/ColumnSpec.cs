using Proofly.Domain.Enums;

namespace Proofly.Connectors.Schemas;

/// <summary>Value type of a source column. The mapper is the only place that gives these types meaning.</summary>
public enum ColumnType
{
    /// <summary>Verbatim text (identifiers are preserved exactly, never trimmed or case-folded).</summary>
    String,
    /// <summary>Decimal string, optional minus, at most 6 fraction digits; paired with a currency column (<see cref="ColumnSpec.CurrencyColumn"/>).</summary>
    Amount,
    /// <summary>Upper-case ISO 4217 code.</summary>
    Currency,
    /// <summary>ISO 8601 calendar date <c>yyyy-MM-dd</c>.</summary>
    Date,
    /// <summary>ISO 8601 date-time with an explicit offset (<c>Z</c> or <c>+hh:mm</c>); a missing offset is rejected.</summary>
    Timestamp,
    /// <summary>UPPER_SNAKE wire value from <see cref="ColumnSpec.AllowedValues"/> (exact, case-sensitive).</summary>
    Enum,
    /// <summary>Non-negative whole number.</summary>
    Integer,
}

public enum AmountSign { Any, NonNegative }

/// <summary>Declarative description of one source column.</summary>
/// <param name="Name">Exact header / element name.</param>
/// <param name="Required">A required column must be present in the header and non-blank in every row.</param>
/// <param name="MaxLength">Maximum length in characters (String, default 200); longer values are INVALID_VALUE.</param>
/// <param name="AllowedValues">Enum wire values.</param>
/// <param name="CurrencyColumn">For Amount: the column holding the ISO 4217 code of this amount.</param>
/// <param name="Sign">For Amount: NonNegative rejects negative values.</param>
public sealed record ColumnSpec(
    string Name, ColumnType Type, bool Required, int? MaxLength = null, IReadOnlyList<string>? AllowedValues = null,
    string? CurrencyColumn = null, AmountSign Sign = AmountSign.Any)
{
    public const int DefaultMaxLength = 200;

    public static ColumnSpec Text(string name, bool required = true, int maxLength = DefaultMaxLength) => new(name, ColumnType.String, required, maxLength);
    public static ColumnSpec Amount(string name, bool required = true, string currencyColumn = "currency", AmountSign sign = AmountSign.Any) =>
        new(name, ColumnType.Amount, required, CurrencyColumn: currencyColumn, Sign: sign);
    public static ColumnSpec Currency(string name = "currency", bool required = true) => new(name, ColumnType.Currency, required);
    public static ColumnSpec Date(string name, bool required = true) => new(name, ColumnType.Date, required);
    public static ColumnSpec Timestamp(string name, bool required = true) => new(name, ColumnType.Timestamp, required);
    public static ColumnSpec Integer(string name, bool required = true) => new(name, ColumnType.Integer, required);
    public static ColumnSpec Enum<T>(string name, bool required = true) where T : struct, Enum =>
        new(name, ColumnType.Enum, required, AllowedValues: System.Enum.GetValues<T>().Select(WireNames.ToUpperSnake).ToArray());
}
