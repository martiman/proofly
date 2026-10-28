using Proofly.Connectors.Abstractions;

namespace Proofly.Connectors.Excel;

/// <summary>Per-source workbook settings (input contract, Excel section).</summary>
public sealed class ExcelSourceOptions
{
    /// <summary>Worksheet to read; null (default) = the first worksheet of the workbook. A named sheet that is missing makes the file unreadable.</summary>
    public string? SheetName { get; set; }
    /// <summary>1-based row holding the column names (default 1). Rows above it are ignored.</summary>
    public int HeaderRow { get; set; } = 1;
    /// <summary>
    /// Excel stores no time zone. When null (default) a date/time cell in a Timestamp column is rejected (INVALID_VALUE);
    /// when configured, that offset is attached explicitly and documented as the source's contract.
    /// </summary>
    public TimeSpan? DefaultTimeZoneOffset { get; set; }
    /// <summary>
    /// False (default): a numeric cell in a String column is rejected ('numeric cell in text column') because leading zeros
    /// of identifiers are already lost. True: whole numbers are passed as their integer text.
    /// </summary>
    public bool AllowNumericText { get; set; }

    public void Validate()
    {
        if (HeaderRow < 1) throw new InvalidOperationException("Excel HeaderRow must be >= 1.");
        if (DefaultTimeZoneOffset is { } o && (o < TimeSpan.FromHours(-14) || o > TimeSpan.FromHours(14) || o.Seconds != 0))
            throw new InvalidOperationException("Excel DefaultTimeZoneOffset must be within +-14:00 in whole minutes.");
    }
}

public sealed class ExcelOptions
{
    public ExcelSourceOptions Default { get; } = new();
    /// <summary>Overrides keyed by manifest source name.</summary>
    public Dictionary<string, ExcelSourceOptions> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ExcelSourceOptions For(string sourceName) => Sources.GetValueOrDefault(sourceName) ?? Default;
}

/// <summary>Content/extension detection of workbook formats.</summary>
public static class WorkbookFormat
{
    /// <summary>Sniffs the first bytes: ZIP 'PK' => Xlsx, OLE2 D0CF11E0 => Xls, otherwise null. The stream position is restored.</summary>
    public static SourceFormat? Sniff(Stream seekable)
    {
        var pos = seekable.Position;
        Span<byte> b = stackalloc byte[4];
        var n = seekable.Read(b);
        seekable.Position = pos;
        if (n >= 4 && b[0] == 0x50 && b[1] == 0x4B) return SourceFormat.Xlsx;
        if (n >= 4 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0) return SourceFormat.Xls;
        return null;
    }

    /// <summary>.xlsx => Xlsx, .xls => Xls; null for other extensions (case-insensitive).</summary>
    public static SourceFormat? FromFileName(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".xlsx" => SourceFormat.Xlsx,
        ".xls" => SourceFormat.Xls,
        _ => null,
    };
}
