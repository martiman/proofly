using System.Text;
using Proofly.Connectors.Abstractions;

namespace Proofly.Connectors.Csv;

/// <summary>Per-source CSV dialect. Delimiter, encoding and decimal format are configured explicitly (input contract); decimals are always '.'.</summary>
public sealed class CsvSourceOptions
{
    /// <summary>Field delimiter; ',' (default) or ';'. Quote is always '"' (RFC 4180).</summary>
    public char Delimiter { get; set; } = ',';
    /// <summary>"utf-8" (default; a BOM is accepted and dropped), "windows-1250" or "iso-8859-2". Undecodable bytes make the file unreadable.</summary>
    public string Encoding { get; set; } = "utf-8";

    public void Validate()
    {
        if (Delimiter is not (',' or ';')) throw new InvalidOperationException($"CSV delimiter must be ',' or ';' (was '{Delimiter}').");
        _ = ResolveEncoding();
    }

    internal System.Text.Encoding ResolveEncoding()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var name = Encoding.ToLowerInvariant();
        if (name is not ("utf-8" or "windows-1250" or "iso-8859-2"))
            throw new InvalidOperationException($"CSV encoding '{Encoding}' is not supported (utf-8, windows-1250, iso-8859-2).");
        return System.Text.Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

public sealed class CsvOptions
{
    public CsvSourceOptions Default { get; } = new();
    /// <summary>Overrides keyed by manifest source name.</summary>
    public Dictionary<string, CsvSourceOptions> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public CsvSourceOptions For(string sourceName) => Sources.GetValueOrDefault(sourceName) ?? Default;
}
