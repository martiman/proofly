using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Proofly.Api.Platform.Contract;

public sealed record ParameterSpec(string Name, string In, bool Required, bool CommaSeparated, string SchemaPointer, string? ItemType, string? Type);

public sealed record ResponseSpec(string? SchemaPointer, bool HasBody);

public sealed class OperationSpec
{
    public required string OperationId { get; init; }
    public required string Method { get; init; }
    public required string Template { get; init; }
    public required Regex PathPattern { get; init; }
    public string? RequiredPermission { get; init; }
    /// <summary>x-required-permission-any: the caller needs any one of these (the handler then enforces the exact permission). Empty = only RequiredPermission.</summary>
    public IReadOnlyList<string> RequiredPermissionAny { get; init; } = [];
    public required IReadOnlyList<ParameterSpec> Parameters { get; init; }
    public bool BodyDeclared { get; init; }
    public bool BodyRequired { get; init; }
    public string? BodySchemaPointer { get; init; }
    public required IReadOnlyDictionary<string, ResponseSpec> Responses { get; init; }
    public required OpenApiDocument Document { get; init; }

    public bool RequiresIfMatch => Parameters.Any(p => p.In == "header" && p.Required && p.Name.Equals("If-Match", StringComparison.OrdinalIgnoreCase));
    public bool RequiresIdempotencyKey => Parameters.Any(p => p.In == "header" && p.Required && p.Name.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One parsed OpenAPI 3.1 document, registered so that JSON Schema $refs resolve against it.</summary>
public sealed class OpenApiDocument
{
    private readonly JsonNode _root;
    private readonly SchemaValidator _validator;
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch"];

    public string BasePath { get; }
    public IReadOnlyList<OperationSpec> Operations { get; }

    public OpenApiDocument(string yaml, string baseUri)
    {
        _root = YamlJson.Parse(new StringReader(yaml)) ?? throw new InvalidDataException("Empty OpenAPI document.");
        _validator = new SchemaValidator(_root);

        var server = _root["servers"]?[0]?["url"]?.GetValue<string>() ?? "";
        var idx = server.IndexOf("://", StringComparison.Ordinal);
        var slash = idx >= 0 ? server.IndexOf('/', idx + 3) : server.IndexOf('/');
        BasePath = slash >= 0 ? server[slash..].TrimEnd('/') : "";
        Operations = ParseOperations();
        AssertSchemasSupported();
    }

    /// <summary>Fails loudly when the document uses a JSON Schema keyword or format the validator does not implement.</summary>
    private void AssertSchemasSupported()
    {
        var schemas = new List<(string, JsonNode)>();
        if (_root["components"]?["schemas"] is JsonObject comps) { foreach (var (n, node) in comps) { schemas.Add(($"/components/schemas/{n}", node!)); } }
        foreach (var pointer in Operations.SelectMany(o => o.Parameters.Select(p => p.SchemaPointer)
            .Concat(o.Responses.Values.Select(r => r.SchemaPointer)).Append(o.BodySchemaPointer)).Where(p => p is not null).Distinct())
        {
            if (Walk(pointer!) is { } node) { schemas.Add((pointer!, node)); }
        }
        var problems = _validator.FindUnsupported(schemas);
        if (problems.Count > 0) { throw new InvalidDataException("The contract uses unsupported JSON Schema features: " + string.Join("; ", problems.Distinct())); }
    }

    /// <summary>Validates a JSON instance against the schema at a pointer in this document. Returns errors (empty = valid).</summary>
    public IReadOnlyList<(string Pointer, string Message)> Validate(string schemaPointer, JsonNode? instance)
    {
        var errors = _validator.Validate(schemaPointer, instance);
        return errors.Count == 0 ? errors : errors.Distinct().ToList();
    }

    private List<OperationSpec> ParseOperations()
    {
        var ops = new List<OperationSpec>();
        if (_root["paths"] is not JsonObject paths) { return ops; }
        foreach (var (template, itemNode) in paths)
        {
            if (itemNode is not JsonObject item) { continue; }
            var pathParams = item["parameters"] as JsonArray;
            foreach (var method in Methods)
            {
                if (item[method] is not JsonObject op) { continue; }
                var pointerBase = $"/paths/{Escape(template)}/{method}";
                var parameters = new List<ParameterSpec>();
                foreach (var (plist, basePtr) in new[] { (pathParams, $"/paths/{Escape(template)}/parameters"), (op["parameters"] as JsonArray, pointerBase + "/parameters") })
                {
                    if (plist is null) { continue; }
                    for (var i = 0; i < plist.Count; i++) { parameters.Add(ParseParameter(plist[i]!, $"{basePtr}/{i}")); }
                }

                var (bodyNode, bodyPointer) = op["requestBody"] is { } rb ? Resolve(rb, pointerBase + "/requestBody") : (null, null);
                string? bodySchema = null;
                if (bodyNode?["content"]?["application/json"]?["schema"] is not null) { bodySchema = bodyPointer + "/content/application~1json/schema"; }

                var responses = new Dictionary<string, ResponseSpec>();
                if (op["responses"] is JsonObject rs)
                {
                    foreach (var (status, rnode) in rs)
                    {
                        var (resolved, pointer) = Resolve(rnode!, pointerBase + "/responses/" + status);
                        var content = resolved?["content"] as JsonObject;
                        string? schema = content?["application/json"]?["schema"] is not null ? pointer + "/content/application~1json/schema" : null;
                        responses[status] = new ResponseSpec(schema, content is { Count: > 0 });
                    }
                }

                ops.Add(new OperationSpec
                {
                    OperationId = op["operationId"]?.GetValue<string>() ?? $"{method} {template}",
                    Method = method.ToUpperInvariant(),
                    Template = template,
                    PathPattern = new Regex("^" + Regex.Replace(Regex.Escape(BasePath + template), @"\\\{[^}]+\}", "[^/]+") + "/?$", RegexOptions.CultureInvariant),
                    RequiredPermission = op["x-required-permission"]?.GetValue<string>(),
                    RequiredPermissionAny = (op["x-required-permission-any"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [],
                    Parameters = parameters,
                    BodyDeclared = op["requestBody"] is not null,
                    BodyRequired = bodyNode?["required"]?.GetValue<bool>() ?? false,
                    BodySchemaPointer = bodySchema,
                    Responses = responses,
                    Document = this,
                });
            }
        }
        return ops;
    }

    private ParameterSpec ParseParameter(JsonNode node, string inlinePointer)
    {
        var (p, pointer) = Resolve(node, inlinePointer);
        if (p is null) { throw new InvalidDataException("Unresolvable parameter."); }
        var schema = p["schema"];
        var type = TypeOf(schema);
        var explode = p["explode"]?.GetValue<bool>() ?? true;
        var commaSeparated = type == "array" && !explode;
        return new ParameterSpec(
            p["name"]!.GetValue<string>(), p["in"]!.GetValue<string>(), p["required"]?.GetValue<bool>() ?? false,
            commaSeparated, pointer + "/schema", type == "array" ? TypeOf(schema?["items"]) : null, type);
    }

    private string? TypeOf(JsonNode? schema)
    {
        if (schema is null) { return null; }
        var (resolved, _) = Resolve(schema, "");
        return resolved?["type"] switch
        {
            JsonArray a => a.Select(x => x?.GetValue<string>()).FirstOrDefault(t => t != "null"),
            JsonValue v => v.GetValue<string>(),
            _ => null,
        };
    }

    /// <summary>Follows local $refs. Returns the node and its JSON pointer (so schemas inside can be addressed).</summary>
    private (JsonNode? Node, string Pointer) Resolve(JsonNode node, string pointerIfInline)
    {
        var current = node;
        var pointer = pointerIfInline;
        for (var i = 0; i < 10 && current is JsonObject o && o["$ref"] is JsonValue r; i++)
        {
            var target = r.GetValue<string>();
            if (!target.StartsWith("#/", StringComparison.Ordinal)) { throw new InvalidDataException($"Only local $ref supported: {target}"); }
            pointer = target[1..];
            current = Walk(pointer);
        }
        return (current, pointer);
    }

    private JsonNode? Walk(string pointer)
    {
        JsonNode? cur = _root;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Replace("~1", "/").Replace("~0", "~");
            cur = cur is JsonObject o ? o[seg] : cur is JsonArray a && int.TryParse(seg, out var ix) ? a[ix] : null;
            if (cur is null) { return null; }
        }
        return cur;
    }

    private static string Escape(string s) => s.Replace("~", "~0").Replace("/", "~1");
}

/// <summary>All loaded contract documents. Matches an HTTP request to its operation.</summary>
public sealed class OpenApiContract
{
    private readonly List<OperationSpec> _operations;

    public OpenApiContract(IEnumerable<OpenApiDocument> documents) => _operations = documents.SelectMany(d => d.Operations).ToList();

    public static OpenApiContract LoadEmbedded(IEnumerable<string>? additionalYaml = null)
    {
        using var s = typeof(OpenApiContract).Assembly.GetManifestResourceStream("openapi.yaml")
            ?? throw new InvalidOperationException("openapi.yaml is not embedded.");
        using var reader = new StreamReader(s);
        var docs = new List<OpenApiDocument> { new(reader.ReadToEnd(), "https://proofly.example/contract/openapi.yaml") };
        var i = 0;
        foreach (var y in additionalYaml ?? []) { docs.Add(new OpenApiDocument(y, $"https://proofly.example/contract/extra-{i++}.yaml")); }
        return new OpenApiContract(docs);
    }

    public OperationSpec? Match(string method, PathString path)
    {
        var p = path.Value ?? "";
        return _operations.FirstOrDefault(o => o.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && o.PathPattern.IsMatch(p));
    }

    public IReadOnlyList<OperationSpec> Operations => _operations;
}
