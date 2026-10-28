namespace Proofly.Connectors.Xml;

/// <summary>Per-source XML settings. Encoding is never configured: it is taken from the BOM / XML declaration.</summary>
public sealed class XmlSourceOptions
{
    /// <summary>
    /// Optional XML namespace of the delivery. When null (default) <c>delivery</c> and <c>record</c> elements must be in
    /// no namespace and the embedded XSDs are used. When set, every element must be in this namespace and the XSD is
    /// generated for it at start-up.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Optional customer-supplied XSD (file path). The document is validated against it IN ADDITION to the built-in
    /// XSD; violations reject the record (or fail the file for root-level violations). It must declare the global
    /// element <c>delivery</c>. External includes/imports are not resolved (no resolver).
    /// </summary>
    public string? CustomSchemaPath { get; set; }

    public void Validate()
    {
        if (Namespace is { Length: 0 }) throw new InvalidOperationException("XML Namespace must be null or a non-empty URI.");
        if (CustomSchemaPath is not null && !File.Exists(CustomSchemaPath))
            throw new InvalidOperationException($"Custom XSD '{CustomSchemaPath}' does not exist.");
    }
}

public sealed class XmlOptions
{
    public XmlSourceOptions Default { get; } = new();
    /// <summary>Overrides keyed by manifest source name.</summary>
    public Dictionary<string, XmlSourceOptions> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public XmlSourceOptions For(string sourceName) => Sources.GetValueOrDefault(sourceName) ?? Default;
}
