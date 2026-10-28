using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Schema;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Xml;

/// <summary>
/// XML adapter for one source (layout: docs/integrations/xml-format.md). It converts each <c>record</c> element into a
/// column-name -> text dictionary and hands it to the schema's RowMapper; it holds no field semantics. A record that
/// violates an XSD is rejected (REQUIRED_FIELD_MISSING when a required element is absent, else INVALID_VALUE) with
/// line/position and the raw excerpt, and reading continues. Structural problems (not well-formed, DOCTYPE, wrong
/// root, source/version/business-date mismatch, unexpected children of the root) make the file unreadable.
/// Row numbers are 1-based record ordinals.
/// </summary>
public sealed class XmlSourceAdapter<TRecord> : ISourceAdapter<TRecord> where TRecord : class, ISourceRecord
{
    private readonly ISourceSchema<TRecord> _schema;
    private readonly XmlSourceOptions _options;
    private readonly Dictionary<string, XmlSchemaSet> _builtIn = new(StringComparer.Ordinal);
    private readonly XmlSchemaSet? _custom;

    public XmlSourceAdapter(ISourceSchema<TRecord> schema, XmlSourceOptions? options = null)
    {
        _schema = schema;
        _options = options ?? new XmlSourceOptions();
        _options.Validate();
        foreach (var v in schema.SupportedVersions)
        {
            var xsd = _options.Namespace is null ? EmbeddedXsd.Read(schema.SourceName, v) : XsdGenerator.Generate(schema, v, _options.Namespace);
            _builtIn[v] = Compile(new StringReader(xsd), _options.Namespace, "built-in XSD");
        }
        if (_options.CustomSchemaPath is { } path)
        {
            using var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            _custom = Compile(r, null, $"custom XSD '{path}'");
        }
    }

    private static XmlSchemaSet Compile(TextReader text, string? ns, string what)
    {
        using var r = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return Compile(r, ns, what);
    }

    private static XmlSchemaSet Compile(XmlReader reader, string? ns, string what)
    {
        var errors = new List<string>();
        var set = new XmlSchemaSet { XmlResolver = null };
        set.ValidationEventHandler += (_, e) => { if (e.Severity == XmlSeverityType.Error) errors.Add(e.Message); };
        try { set.Add(ns, reader); set.Compile(); }
        catch (XmlException e) { errors.Add(e.Message); }
        catch (XmlSchemaException e) { errors.Add(e.Message); }
        if (errors.Count > 0) throw new InvalidOperationException($"The {what} is not a valid schema: {string.Join("; ", errors)}");
        return set;
    }

    public string SourceName => _schema.SourceName;
    public SourceFormat Format => SourceFormat.Xml;
    public IReadOnlyCollection<string> SupportedSchemaVersions => _schema.SupportedVersions;

    private XmlDeliveryReader Open(Stream content, string version)
    {
        var sets = new List<XmlSchemaSet> { _builtIn[version] };
        if (_custom is not null) sets.Add(_custom);
        return new XmlDeliveryReader(content, _options.Namespace, sets);
    }

    /// <summary>Null when the root is acceptable, otherwise the file-level problem.</summary>
    private string? CheckRoot(RootItem root, SourceDescriptor d)
    {
        string At() => $" (line {root.Line}, position {root.Position})";
        if (root.LocalName != "delivery") return $"Root element must be <delivery> but is <{root.LocalName}>{At()}.";
        var ns = _options.Namespace ?? "";
        if (root.Namespace != ns) return $"Root element namespace must be '{ns}' but is '{root.Namespace}'{At()}.";
        string? Attr(string n) => root.Attributes.GetValueOrDefault(n);
        if (Attr("source") != SourceName) return $"Attribute source must be '{SourceName}' but is '{Attr("source") ?? "(missing)"}'{At()}.";
        var version = Attr("schemaVersion");
        if (version is null) return $"Attribute schemaVersion is missing{At()}.";
        if (!_schema.SupportedVersions.Contains(version))
            return $"Schema version '{version}' is not supported for '{SourceName}' (supported: {string.Join(", ", _schema.SupportedVersions)}){At()}.";
        if (version != d.SchemaVersion) return $"Document declares schemaVersion '{version}' but the manifest declares '{d.SchemaVersion}'{At()}.";
        var date = Attr("businessDate");
        if (date != d.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return $"Attribute businessDate '{date ?? "(missing)"}' does not match the manifest business date {d.BusinessDate:yyyy-MM-dd}{At()}.";
        if (root.Issues.Count > 0) return $"Root element is invalid at line {root.Issues[0].Line}, position {root.Issues[0].Position}: {root.Issues[0].Message}";
        return null;
    }

    public async Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default)
    {
        if (!_schema.SupportedVersions.Contains(descriptor.SchemaVersion))
            return SchemaValidation.Fail($"Schema version '{descriptor.SchemaVersion}' is not supported for '{SourceName}' (supported: {string.Join(", ", _schema.SupportedVersions)}).");
        await foreach (var item in Open(content, descriptor.SchemaVersion).ReadAsync(ct).WithCancellation(ct))
        {
            if (item is not RootItem root) break;
            return CheckRoot(root, descriptor) is { } problem ? SchemaValidation.Fail(problem) : SchemaValidation.Ok;
        }
        return SchemaValidation.Fail("XML document has no root element.");
    }

    public async IAsyncEnumerable<ParseResult<TRecord>> ParseAsync(Stream content, SourceDescriptor descriptor, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_schema.SupportedVersions.Contains(descriptor.SchemaVersion))
            throw new SourceFormatException($"Schema version '{descriptor.SchemaVersion}' is not supported for '{SourceName}'.");
        var columns = _schema.ColumnsFor(descriptor.SchemaVersion);
        await foreach (var item in Open(content, descriptor.SchemaVersion).ReadAsync(ct).WithCancellation(ct))
        {
            if (item is RootItem root)
            {
                if (CheckRoot(root, descriptor) is { } problem) throw new SourceFormatException(problem);
                continue;
            }
            var rec = (RecordItem)item;
            var mapped = _schema.Mapper.Map(descriptor.SchemaVersion, rec.Number, rec.Cells, rec.Raw);
            if (rec.Issues.Count == 0) { yield return mapped; continue; }

            var rowId = mapped.Rejection?.SourceRowId
                ?? (_schema.RowIdColumn is { } idc && rec.Cells.TryGetValue(idc, out var idv) && !string.IsNullOrWhiteSpace(idv) ? idv : null);
            var detail = string.Join(" | ", rec.Issues.Take(3).Select(i => $"line {i.Line}, position {i.Position}: {i.Message}"));
            var missing = columns.FirstOrDefault(c => c.Required && !rec.Cells.ContainsKey(c.Name));
            var field = rec.Issues.Select(i => i.Element).FirstOrDefault(e => e is not null && columns.Any(c => c.Name == e));
            yield return missing is not null
                ? ParseResult<TRecord>.Reject(new RowRejection(ReasonCodes.RequiredFieldMissing, $"{missing.Name} is missing (XSD validation failed, record at line {rec.Line}: {detail})", rec.Number, rec.Raw, rowId, missing.Name, mapped.Rejection?.ControlAmount))
                : ParseResult<TRecord>.Reject(new RowRejection(ReasonCodes.InvalidValue, $"XSD validation failed, record at line {rec.Line}: {detail}", rec.Number, rec.Raw, rowId, field, mapped.Rejection?.ControlAmount));
        }
    }
}
