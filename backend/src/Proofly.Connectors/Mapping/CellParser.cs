using System.Globalization;
using System.Text.RegularExpressions;
using Proofly.Connectors.Schemas;

namespace Proofly.Connectors.Mapping;

/// <summary>
/// Strict, culture-invariant parsing of one non-blank cell. Rules (documented contract): no trimming, no repair
/// (a value with leading/trailing whitespace is invalid for every type except String, which is kept verbatim);
/// decimals are <c>-?digits[.digits]</c> with at most 6 fraction digits (no exponent, no thousands separator, no '+');
/// dates are <c>yyyy-MM-dd</c>; timestamps are ISO 8601 with an explicit offset; enums match the UPPER_SNAKE value exactly.
/// </summary>
internal static partial class CellParser
{
    [GeneratedRegex("^-?[0-9]+(\\.[0-9]+)?$", RegexOptions.CultureInvariant)] private static partial Regex DecimalRx();
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant)] private static partial Regex DateRx();
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)] private static partial Regex TsRx();
    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)] private static partial Regex IntRx();

    public static bool IsBlank(string? cell) => string.IsNullOrWhiteSpace(cell);

    /// <summary>Returns the typed value (string, decimal, DateOnly, DateTimeOffset, int) or an error text.</summary>
    public static (object? Value, string? Error) Parse(ColumnSpec spec, string text)
    {
        if (spec.Type != ColumnType.String && text != text.Trim()) return (null, "has leading or trailing whitespace");
        switch (spec.Type)
        {
            case ColumnType.String:
                var max = spec.MaxLength ?? ColumnSpec.DefaultMaxLength;
                return text.Length > max ? (null, $"is longer than {max} characters") : (text, null);
            case ColumnType.Amount:
                if (!TryDecimal(text, out var d, out var derr)) return (null, derr);
                if (spec.Sign == AmountSign.NonNegative && d < 0) return (null, "must not be negative");
                return (d, null);
            case ColumnType.Currency:
                return IsCurrency(text) ? (text, null) : (null, $"'{Excerpt(text)}' is not a valid ISO 4217 code");
            case ColumnType.Date:
                return DateRx().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (date, null) : (null, "is not an ISO 8601 date (yyyy-MM-dd)");
            case ColumnType.Timestamp:
                if (!TsRx().IsMatch(text)) return (null, "is not an ISO 8601 timestamp with an explicit offset");
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
                    ? (ts, null) : (null, "is not a valid ISO 8601 timestamp");
            case ColumnType.Enum:
                return spec.AllowedValues!.Contains(text, StringComparer.Ordinal)
                    ? (text, null) : (null, $"'{Excerpt(text)}' is not one of {string.Join(", ", spec.AllowedValues!)}");
            case ColumnType.Integer:
                return IntRx().IsMatch(text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                    ? (i, null) : (null, "is not a non-negative whole number");
            default:
                throw new InvalidOperationException($"Unhandled column type {spec.Type}.");
        }
    }

    public static bool TryDecimal(string text, out decimal value, out string error)
    {
        value = 0; error = "";
        if (!DecimalRx().IsMatch(text)) { error = "is not a decimal number (digits with optional minus and '.')"; return false; }
        var dot = text.IndexOf('.');
        if (dot >= 0 && text.Length - dot - 1 > 6) { error = "has more than 6 fraction digits"; return false; }
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) { error = "is out of range"; return false; }
        return true;
    }

    public static bool IsCurrency(string text) => text.Length == 3 && text.All(c => c is >= 'A' and <= 'Z') && Iso4217.IsValid(text);

    public static string Excerpt(string text) => text.Length > 40 ? text[..40] + "..." : text;
}
