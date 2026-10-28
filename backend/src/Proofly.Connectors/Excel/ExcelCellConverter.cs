using System.Globalization;
using Proofly.Connectors.Schemas;

namespace Proofly.Connectors.Excel;

/// <summary>
/// Turns one spreadsheet cell into the text the shared RowMapper expects, depending on the target column type. It
/// never repairs: anything that cannot be represented exactly is an error with a reason. Field semantics stay in the mapper.
/// </summary>
/// <summary>An Excel error cell (#DIV/0!, #N/A, ...). ExcelDataReader reports its value as null; the adapter keeps the error visible.</summary>
internal sealed record ExcelCellError(string Code);

internal static class ExcelCellConverter
{
    /// <summary>Returns (text, null) on success (null text = blank) or (null, error).</summary>
    public static (string? Text, string? Error) Convert(object? cell, ColumnType type, ExcelSourceOptions o)
    {
        switch (cell)
        {
            case null:
                return (null, null);
            case string s:
                return (s, null);
            case ExcelCellError e:
                return (null, $"error cell {e.Code} is not allowed");
            case bool:
                return (null, "boolean cell is not allowed");
            case double d:
                return Number(d, type, o);
            case int i:
                return Number(i, type, o);
            case long l:
                return Number(l, type, o);
            case decimal m:
                return NumberFromDecimal(m, type, o);
            case DateTime dt:
                return Date(dt, type, o);
            default:
                return (null, $"unsupported cell type {cell.GetType().Name} (error cell or similar)");
        }
    }

    private static (string?, string?) Number(double d, ColumnType type, ExcelSourceOptions o)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return (null, "numeric cell is not a finite number");
        switch (type)
        {
            case ColumnType.Amount:
            case ColumnType.Integer:
                return ExactDecimal(d);
            case ColumnType.String when o.AllowNumericText:
                var (t, e) = ExactDecimal(d);
                return e is null && !t!.Contains('.') ? (t, null) : (null, e ?? "numeric cell with fraction in text column");
            case ColumnType.String:
                return (null, "numeric cell in text column");
            default:
                return (null, $"numeric cell in {type} column");
        }
    }

    private static (string?, string?) Number(long v, ColumnType type, ExcelSourceOptions o) => NumberFromDecimal(v, type, o);

    private static (string?, string?) NumberFromDecimal(decimal m, ColumnType type, ExcelSourceOptions o) => type switch
    {
        ColumnType.Amount or ColumnType.Integer => (m.ToString(CultureInfo.InvariantCulture), null),
        ColumnType.String when o.AllowNumericText && m == decimal.Truncate(m) => (m.ToString(CultureInfo.InvariantCulture), null),
        ColumnType.String => (null, "numeric cell in text column"),
        _ => (null, $"numeric cell in {type} column"),
    };

    /// <summary>
    /// The double Excel stores is rendered with the shortest round-trippable text ("R"); it is accepted only when that
    /// text is a decimal with at most 6 fraction digits that converts back to exactly the same double.
    /// </summary>
    internal static (string?, string?) ExactDecimal(double d)
    {
        const string precision = "excel float precision: numeric cell cannot be represented exactly as a decimal with at most 6 fraction digits";
        decimal m;
        try { m = decimal.Parse(d.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture); }
        catch (OverflowException) { return (null, precision); }
        if ((double)m != d) return (null, precision);
        var scale = (decimal.GetBits(m)[3] >> 16) & 0xFF;
        if (scale > 6) return (null, precision);
        return (m.ToString(CultureInfo.InvariantCulture), null);
    }

    private static (string?, string?) Date(DateTime dt, ColumnType type, ExcelSourceOptions o)
    {
        switch (type)
        {
            case ColumnType.Date:
                return dt.TimeOfDay == TimeSpan.Zero
                    ? (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
                    : (null, "date cell has a time component in a date column");
            case ColumnType.Timestamp:
                if (o.DefaultTimeZoneOffset is not { } off)
                    return (null, "date-time cell has no time zone offset (Excel stores none) and DefaultTimeZoneOffset is not configured");
                return (new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), off).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture), null);
            default:
                return (null, $"date cell in {type} column");
        }
    }
}
