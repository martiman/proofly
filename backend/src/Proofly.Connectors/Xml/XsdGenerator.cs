using System.Text;
using System.Xml;
using System.Xml.Linq;
using Proofly.Connectors.Schemas;

namespace Proofly.Connectors.Xml;

/// <summary>
/// Generates the XSD of one source/version from the shared <see cref="ISourceSchema"/> column specs, so the XSD can
/// never disagree with the CSV contract. The XSD is structural and lexical only (element names, required/optional,
/// lengths, enumerations, the shape of dates/timestamps/amounts/currencies); value semantics (valid calendar date,
/// ISO 4217 membership, cross-field rules) stay in the RowMapper. Every typed element accepts an empty value: a
/// blank required value is reported by the mapper as REQUIRED_FIELD_MISSING. Output is deterministic (LF, UTF-8).
/// </summary>
public static class XsdGenerator
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private const string DatePattern = @"\d{4}-\d{2}-\d{2}";
    private const string TimestampPattern = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})";
    private const string AmountPattern = @"-?\d+(\.\d{1,6})?";

    public static string Generate(ISourceSchema schema, string version, string? targetNamespace = null)
    {
        var columns = schema.ColumnsFor(version);
        var all = new XElement(Xs + "all");
        foreach (var c in columns) all.Add(Element(c.Name, c.Required, Type(c)));
        if (schema.DynamicColumnPrefix is { } prefix)
        {
            // open-ended optional columns (manifest declared_total_<ccy>): one element per valid lower-case ISO 4217 code
            for (var a = 'A'; a <= 'Z'; a++)
                for (var b = 'A'; b <= 'Z'; b++)
                    for (var d = 'A'; d <= 'Z'; d++)
                    {
                        var code = new string([a, b, d]);
                        if (Iso4217.IsValid(code)) all.Add(Element(prefix + code.ToLowerInvariant(), false, Pattern(AmountPattern)));
                    }
        }

        var record = new XElement(Xs + "element", new XAttribute("name", "record"), new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
            new XElement(Xs + "complexType", all));
        var delivery = new XElement(Xs + "element", new XAttribute("name", "delivery"),
            new XElement(Xs + "complexType",
                new XElement(Xs + "sequence", record),
                Attr("source", Fixed(schema.SourceName), true),
                Attr("schemaVersion", Fixed(version), true),
                Attr("businessDate", Pattern(DatePattern, "xs:date"), true)));

        var root = new XElement(Xs + "schema", new XAttribute(XNamespace.Xmlns + "xs", Xs.NamespaceName),
            new XAttribute("elementFormDefault", "qualified"));
        if (targetNamespace is not null) root.Add(new XAttribute("targetNamespace", targetNamespace));
        root.Add(new XComment($" {schema.SourceName} v{version}: generated from the shared column specs; do not edit. "), delivery);

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", OmitXmlDeclaration = true };
        using (var w = XmlWriter.Create(sb, settings)) root.WriteTo(w);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + sb + "\n";
    }

    private static XElement Element(string name, bool required, XElement simpleType) =>
        new(Xs + "element", new XAttribute("name", name), required ? null : new XAttribute("minOccurs", "0"), simpleType);

    private static XElement Attr(string name, XElement simpleType, bool required) =>
        new(Xs + "attribute", new XAttribute("name", name), required ? new XAttribute("use", "required") : null, simpleType);

    private static XElement Fixed(string value) =>
        new(Xs + "simpleType", new XElement(Xs + "restriction", new XAttribute("base", "xs:string"), new XElement(Xs + "enumeration", new XAttribute("value", value))));

    private static XElement Pattern(string pattern, string baseType = "xs:string") =>
        new(Xs + "simpleType", new XElement(Xs + "restriction", new XAttribute("base", baseType),
            new XElement(Xs + "pattern", new XAttribute("value", baseType == "xs:string" ? $"({pattern})?" : pattern))));

    private static XElement Type(ColumnSpec c) => c.Type switch
    {
        ColumnType.String => new XElement(Xs + "simpleType", new XElement(Xs + "restriction", new XAttribute("base", "xs:string"),
            new XElement(Xs + "maxLength", new XAttribute("value", (c.MaxLength ?? ColumnSpec.DefaultMaxLength).ToString(System.Globalization.CultureInfo.InvariantCulture))))),
        ColumnType.Amount => Pattern(AmountPattern),
        ColumnType.Currency => Pattern("[A-Z]{3}"),
        ColumnType.Date => Pattern(DatePattern),
        ColumnType.Timestamp => Pattern(TimestampPattern),
        ColumnType.Integer => Pattern(@"\d+"),
        ColumnType.Enum => new XElement(Xs + "simpleType", new XElement(Xs + "restriction", new XAttribute("base", "xs:string"),
            new[] { "" }.Concat(c.AllowedValues!).Select(v => new XElement(Xs + "enumeration", new XAttribute("value", v))))),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };
}

/// <summary>The generated XSDs shipped as embedded resources (<c>Xml/Xsd/&lt;source&gt;.&lt;version&gt;.xsd</c>).</summary>
public static class EmbeddedXsd
{
    public static string ResourceName(string source, string version) => $"Proofly.Connectors.Xml.Xsd.{source}.{version}.xsd";

    public static string Read(string source, string version)
    {
        using var s = typeof(EmbeddedXsd).Assembly.GetManifestResourceStream(ResourceName(source, version))
            ?? throw new InvalidOperationException($"Embedded XSD for '{source}' v{version} is missing.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
