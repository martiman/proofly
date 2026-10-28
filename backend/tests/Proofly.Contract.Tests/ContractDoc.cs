using System.Text.Json.Nodes;
using Proofly.Api.Platform.Contract;
using YamlDotNet.RepresentationModel;

namespace Proofly.Contract.Tests;

/// <summary>One contract operation with everything the tests assert on.</summary>
internal sealed record Op(
    string OperationId, string Method, string Template, string Path, string? Permission, string? AuditEvent,
    IReadOnlyList<string> RequiredHeaders, string? SuccessSchemaRef, OperationSpec Spec)
{
    /// <summary>Permissions that let a caller past the platform check: the primary one plus x-required-permission-any.</summary>
    public IReadOnlyList<string> AcceptedPermissions => Permission is null ? Spec.RequiredPermissionAny : [Permission, .. Spec.RequiredPermissionAny];
    public bool IsMutation => Method != "GET";
    public override string ToString() => OperationId;

    /// <summary>The request path with every path parameter replaced by a syntactically valid placeholder.</summary>
    public string FilledPath(IReadOnlyDictionary<string, string>? values = null) =>
        System.Text.RegularExpressions.Regex.Replace(Path, @"\{([^}]+)\}", m => values is not null && values.TryGetValue(m.Groups[1].Value, out var v) ? v : "x");
}

internal static class ContractDoc
{
    public static string FilePath { get; } = FindFile();
    public static string Text { get; } = File.ReadAllText(FilePath);
    public static JsonObject Root { get; } = (JsonObject)ToJson(Load(Text))!;
    public static OpenApiContract Contract { get; } = OpenApiContract.LoadEmbedded();
    public static IReadOnlyList<Op> Ops { get; } = BuildOps();

    public static IEnumerable<object[]> OpData() => Ops.Select(o => new object[] { o.OperationId });
    public static Op ById(string id) => Ops.Single(o => o.OperationId == id);

    public static IReadOnlyList<string> EnumOf(string schema) =>
        ((JsonArray)Root["components"]!["schemas"]![schema]!["enum"]!).Select(n => (string)n!).ToList();

    private static string FindFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "docs", "integrations", "api", "openapi.yaml");
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new FileNotFoundException("docs/integrations/api/openapi.yaml not found above the test output directory.");
    }

    private static YamlNode Load(string yaml)
    {
        var s = new YamlStream();
        s.Load(new StringReader(yaml));
        return s.Documents[0].RootNode;
    }

    // Scalars stay strings: the tests only read names, ids and enum members.
    private static JsonNode? ToJson(YamlNode n) => n switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, ToJson(kv.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(ToJson).ToArray()),
        YamlScalarNode sc => JsonValue.Create(sc.Value),
        _ => null,
    };

    private static JsonNode? Resolve(JsonNode? node)
    {
        for (var i = 0; i < 10 && node is JsonObject o && o["$ref"] is JsonNode r; i++)
        {
            node = Root;
            foreach (var seg in ((string)r!).TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries)) { node = node?[seg.Replace("~1", "/").Replace("~0", "~")]; }
        }
        return node;
    }

    private static List<Op> BuildOps()
    {
        var specs = Contract.Operations.ToDictionary(o => o.OperationId);
        var ops = new List<Op>();
        foreach (var (template, item) in (JsonObject)Root["paths"]!)
        {
            foreach (var method in new[] { "get", "put", "post", "delete", "patch" })
            {
                if (item![method] is not JsonObject op) { continue; }
                var spec = specs[(string)op["operationId"]!];
                var success = ((JsonObject)op["responses"]!).FirstOrDefault(kv => kv.Key.StartsWith('2')).Value;
                var schemaRef = (string?)Resolve(success)?["content"]?["application/json"]?["schema"]?["$ref"];
                ops.Add(new Op(spec.OperationId, spec.Method, template, spec.Document.BasePath + template,
                    (string?)op["x-required-permission"], (string?)op["x-audit-event"],
                    spec.Parameters.Where(p => p.In == "header" && p.Required).Select(p => p.Name).ToList(), schemaRef?.Split('/').Last(), spec));
            }
        }
        return ops;
    }
}
