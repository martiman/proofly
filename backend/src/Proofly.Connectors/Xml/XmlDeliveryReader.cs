using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using Proofly.Connectors.Abstractions;

namespace Proofly.Connectors.Xml;

internal sealed record XsdIssue(string Message, int Line, int Position, string? Element);

internal abstract record XmlItem;

/// <summary>The <c>delivery</c> start tag, after validation of the root against every XSD.</summary>
internal sealed record RootItem(string LocalName, string Namespace, IReadOnlyDictionary<string, string> Attributes, int Line, int Position, IReadOnlyList<XsdIssue> Issues) : XmlItem;

/// <summary>One <c>record</c> element: column name -> text, plus the XSD violations found while reading it.</summary>
internal sealed record RecordItem(int Number, int Line, int Position, IReadOnlyDictionary<string, string?> Cells, string Raw, IReadOnlyList<XsdIssue> Issues) : XmlItem;

/// <summary>
/// Streaming reader: a non-validating, DTD-prohibiting <see cref="XmlReader"/> drives one <see cref="XmlSchemaValidator"/>
/// per XSD (built-in and optional customer XSD) so violations can be attributed to the record being read. Only one
/// record is held in memory at a time. Whitespace between elements is formatting and dropped; text inside a column
/// element is kept verbatim.
/// </summary>
internal sealed class XmlDeliveryReader
{
    private const int MaxValueChars = 1_000_000;
    private const int MaxRawChars = 600;
    private const string XmlnsNs = "http://www.w3.org/2000/xmlns/";

    private sealed class Pass
    {
        public required XmlSchemaValidator Validator;
        public readonly List<XsdIssue> Issues = [];
    }

    private readonly XmlReader _reader;
    private readonly List<Pass> _passes = [];
    private string? _currentElement;

    public XmlDeliveryReader(Stream content, string? expectedNamespace, IEnumerable<XmlSchemaSet> schemaSets)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, // no DOCTYPE at all: no entities, no billion laughs, no XXE
            XmlResolver = null,                     // never resolve anything external
            ValidationType = ValidationType.None,
            Async = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
            MaxCharactersInDocument = 0,
        };
        _reader = XmlReader.Create(content, settings);
        foreach (var set in schemaSets)
        {
            var pass = new Pass { Validator = null! };
            var v = new XmlSchemaValidator(_reader.NameTable, set, new XmlNamespaceManager(_reader.NameTable), XmlSchemaValidationFlags.None)
            {
                XmlResolver = null,
                LineInfoProvider = (IXmlLineInfo)_reader,
            };
            v.ValidationEventHandler += (_, e) =>
            {
                if (e.Severity != XmlSeverityType.Error) return;
                pass.Issues.Add(new XsdIssue(e.Message, e.Exception?.LineNumber ?? Line, e.Exception?.LinePosition ?? Position, _currentElement));
            };
            v.Initialize();
            pass.Validator = v;
            _passes.Add(pass);
        }
        _ = expectedNamespace;
    }

    private int Line => ((IXmlLineInfo)_reader).LineNumber;
    private int Position => ((IXmlLineInfo)_reader).LinePosition;

    private static SourceFormatException Format(XmlException e) =>
        new($"XML is not well-formed or not allowed at line {e.LineNumber}, position {e.LinePosition}: {e.Message}", e);

    private async Task<bool> ReadNodeAsync(CancellationToken ct)
    {
        try { return await _reader.ReadAsync().WaitAsync(ct); }
        catch (XmlException e) { throw Format(e); }
    }

    private async Task<string> ValueAsync()
    {
        try { return await _reader.GetValueAsync(); }
        catch (XmlException e) { throw Format(e); }
    }

    private List<XsdIssue> Drain()
    {
        var all = new List<XsdIssue>();
        foreach (var p in _passes) { all.AddRange(p.Issues); p.Issues.Clear(); }
        return all;
    }

    public async IAsyncEnumerable<XmlItem> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var number = 0;
        Dictionary<string, string?>? cells = null;
        StringBuilder? raw = null;
        StringBuilder? text = null;
        string? column = null;
        var recordLine = 0;
        var recordPos = 0;
        var extra = new List<XsdIssue>();
        var sawRoot = false;

        while (await ReadNodeAsync(ct))
        {
            switch (_reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var depth = _reader.Depth;
                    var local = _reader.LocalName;
                    var ns = _reader.NamespaceURI;
                    var line = Line;
                    var pos = Position;
                    var empty = _reader.IsEmptyElement;
                    var attrs = new List<(string Local, string Ns, string Value)>();
                    if (_reader.HasAttributes)
                    {
                        while (_reader.MoveToNextAttribute())
                        {
                            if (_reader.NamespaceURI == XmlnsNs) continue;
                            attrs.Add((_reader.LocalName, _reader.NamespaceURI, await ValueAsync()));
                        }
                        _reader.MoveToElement();
                    }
                    if (depth == 1) { Drain(); extra.Clear(); }
                    _currentElement = local;
                    foreach (var p in _passes)
                    {
                        p.Validator.ValidateElement(local, ns, null);
                        foreach (var a in attrs) p.Validator.ValidateAttribute(a.Local, a.Ns, a.Value, null);
                        p.Validator.ValidateEndOfAttributes(null);
                    }
                    if (depth == 0)
                    {
                        sawRoot = true;
                        yield return new RootItem(local, ns, attrs.Where(a => a.Ns.Length == 0).ToDictionary(a => a.Local, a => a.Value, StringComparer.Ordinal), line, pos, Drain());
                    }
                    else if (depth == 1)
                    {
                        if (local != "record") throw new SourceFormatException($"Unexpected element <{local}> under <delivery> at line {line}, position {pos}; only <record> is allowed.");
                        number++;
                        recordLine = line; recordPos = pos;
                        cells = new Dictionary<string, string?>(StringComparer.Ordinal);
                        raw = new StringBuilder("<record>");
                        column = null;
                    }
                    else if (depth == 2)
                    {
                        column = local;
                        text = new StringBuilder();
                        if (cells!.ContainsKey(local)) extra.Add(new XsdIssue($"duplicate element '{local}'", line, pos, local));
                        if (attrs.Count > 0) extra.Add(new XsdIssue($"element '{local}' must not have attributes", line, pos, local));
                    }
                    else if (depth == 3)
                    {
                        extra.Add(new XsdIssue($"element '{column}' must contain text only", line, pos, column));
                    }
                    if (empty)
                    {
                        var rec = EndElement(depth, cells, raw, ref text, ref column, extra, number, recordLine, recordPos);
                        if (rec is not null) yield return rec;
                    }
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                {
                    var depth = _reader.Depth; // = depth of the containing element + 1
                    var isWs = _reader.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;
                    var value = await ValueAsync();
                    if (depth == 3 && text is not null)
                    {
                        // value of a column element: verbatim, never trimmed
                        if (text.Length + value.Length > MaxValueChars)
                            throw new SourceFormatException($"Element '{column}' near line {Line} exceeds {MaxValueChars} characters.");
                        text.Append(value);
                        foreach (var p in _passes) p.Validator.ValidateText(value);
                    }
                    else if (isWs)
                    {
                        foreach (var p in _passes) p.Validator.ValidateWhitespace(value); // formatting between elements
                    }
                    else
                    {
                        foreach (var p in _passes) p.Validator.ValidateText(value);
                        if (depth <= 2) extra.Add(new XsdIssue("text is not allowed between elements", Line, Position, null));
                    }
                    break;
                }
                case XmlNodeType.EndElement:
                {
                    var rec = EndElement(_reader.Depth, cells, raw, ref text, ref column, extra, number, recordLine, recordPos);
                    if (rec is not null) yield return rec;
                    break;
                }
            }
        }
        if (!sawRoot) throw new SourceFormatException("XML document has no root element.");
        foreach (var p in _passes) p.Validator.EndValidation();
        if (Drain() is { Count: > 0 } tail)
            throw new SourceFormatException($"XML document is invalid at line {tail[0].Line}, position {tail[0].Position}: {tail[0].Message}");
    }

    /// <summary>Handles an end tag (or the implicit end of an empty element); returns the record when a record ended.</summary>
    private XmlItem? EndElement(int depth, Dictionary<string, string?>? cells, StringBuilder? raw, ref StringBuilder? text, ref string? column,
        List<XsdIssue> extra, int number, int recordLine, int recordPos)
    {
        foreach (var p in _passes) p.Validator.ValidateEndElement(null);
        if (depth == 2 && column is not null)
        {
            var value = text!.ToString();
            cells!.TryAdd(column, value);
            if (raw!.Length < MaxRawChars) raw.Append('<').Append(column).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(column).Append('>');
            text = null; column = null;
            _currentElement = "record";
            return null;
        }
        if (depth == 1)
        {
            raw!.Append("</record>");
            var issues = Drain();
            issues.AddRange(extra);
            extra.Clear();
            var r = raw.Length > MaxRawChars ? raw.ToString(0, MaxRawChars) + "..." : raw.ToString();
            return new RecordItem(number, recordLine, recordPos, cells!, r, issues);
        }
        return null;
    }
}
