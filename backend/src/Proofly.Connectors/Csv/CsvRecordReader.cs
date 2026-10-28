using System.Text;
using Proofly.Connectors.Abstractions;

namespace Proofly.Connectors.Csv;

/// <summary>One physical CSV record. <see cref="Problem"/> is set for malformed quoting (the record is still delimited so the reader can continue).</summary>
public sealed record CsvRecord(IReadOnlyList<string> Fields, string Raw, string? Problem)
{
    /// <summary>An empty line (no characters at all).</summary>
    public bool IsBlank => Raw.Length == 0 && Problem is null;
}

/// <summary>
/// Streaming RFC 4180 reader (own implementation; no third-party dependency). Quoted fields may contain the delimiter,
/// quotes (<c>""</c>) and CR/LF; CRLF and LF both end a record. Strict: a quote inside an unquoted field or text after a
/// closing quote marks the record malformed (never repaired). An unterminated quote at end of input makes the file
/// unreadable (SourceFormatException). A leading BOM character is dropped.
/// </summary>
public sealed class CsvRecordReader(TextReader reader, char delimiter)
{
    private const int RawCap = 2000;
    private readonly char[] _buf = new char[8192];
    private int _pos, _len;
    private bool _started;

    private async ValueTask<int> PeekAsync(CancellationToken ct)
    {
        if (_pos >= _len)
        {
            _len = await reader.ReadAsync(_buf.AsMemory(), ct);
            _pos = 0;
            if (_len == 0) return -1;
        }
        return _buf[_pos];
    }

    private async ValueTask<int> ReadCharAsync(CancellationToken ct)
    {
        var c = await PeekAsync(ct);
        if (c >= 0) _pos++;
        return c;
    }

    /// <summary>Next record, or null at end of input.</summary>
    public async ValueTask<CsvRecord?> ReadAsync(CancellationToken ct = default)
    {
        if (!_started)
        {
            _started = true;
            if (await PeekAsync(ct) == '﻿') _pos++;
        }
        if (await PeekAsync(ct) < 0) return null;

        var fields = new List<string>();
        var field = new StringBuilder();
        var raw = new StringBuilder();
        string? problem = null;
        bool inQuotes = false, quoted = false, afterQuote = false;

        void Raw(char c) { if (raw.Length < RawCap) raw.Append(c); }
        void EndField() { fields.Add(field.ToString()); field.Clear(); quoted = false; afterQuote = false; }

        while (true)
        {
            var ci = await ReadCharAsync(ct);
            if (ci < 0)
            {
                if (inQuotes) throw new SourceFormatException("CSV file ends inside a quoted field (unterminated quote).");
                EndField();
                break;
            }
            var c = (char)ci;
            if (inQuotes)
            {
                Raw(c);
                if (c == '"')
                {
                    if (await PeekAsync(ct) == '"') { _pos++; Raw('"'); field.Append('"'); }
                    else { inQuotes = false; afterQuote = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == delimiter) { Raw(c); EndField(); continue; }
            if (c is '\n' or '\r')
            {
                if (c == '\r' && await PeekAsync(ct) == '\n') _pos++;
                EndField();
                break;
            }
            Raw(c);
            if (c == '"')
            {
                if (field.Length == 0 && !quoted && !afterQuote) { inQuotes = true; quoted = true; }
                else { problem ??= "a quote character appears inside an unquoted field or after a closing quote"; field.Append(c); }
            }
            else
            {
                if (afterQuote) problem ??= "text follows the closing quote of a quoted field";
                field.Append(c);
            }
        }
        return new CsvRecord(fields, raw.ToString(), problem);
    }
}
