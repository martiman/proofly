using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Proofly.Api.Platform.Contract;

namespace Proofly.Security.Tests.Support;

/// <summary>The embedded OpenAPI contract: operations (with x-required-permission) and a generator of minimal valid requests.</summary>
public static class Contract
{
    private static readonly Lazy<OpenApiContract> Loaded = new(() => OpenApiContract.LoadEmbedded());
    private static readonly Lazy<JsonNode> Root = new(() =>
    {
        var t = typeof(OpenApiContract).Assembly.GetType("Proofly.Api.Platform.Contract.YamlJson")!;
        using var s = typeof(OpenApiContract).Assembly.GetManifestResourceStream("openapi.yaml")!;
        using var r = new StreamReader(s);
        return (JsonNode)t.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [r])!;
    });

    public static IReadOnlyList<OperationSpec> Operations => Loaded.Value.Operations;
    public static IEnumerable<OperationSpec> WithPermission => Operations.Where(o => o.RequiredPermission is not null);
    public static bool IsMutating(OperationSpec o) => o.Method != "GET";
    public static IReadOnlyList<string> PathParams(OperationSpec o) => Regex.Matches(o.Template, @"\{([^}]+)\}").Select(m => m.Groups[1].Value).ToList();

    private static JsonNode? At(string pointer)
    {
        JsonNode? cur = Root.Value;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Replace("~1", "/").Replace("~0", "~");
            cur = cur is JsonObject o ? o[seg] : cur is JsonArray a && int.TryParse(seg, out var i) ? a[i] : null;
            if (cur is null) { return null; }
        }
        return cur;
    }

    private static JsonNode Deref(JsonNode n)
    {
        for (var i = 0; i < 10 && n is JsonObject o && o["$ref"] is JsonValue r; i++) { n = At(r.GetValue<string>()[1..])!; }
        return n;
    }

    /// <summary>Request path with the given path-parameter values and any required query parameters filled in.</summary>
    public static string Url(OperationSpec o, Func<string, string> pathValue, bool requiredQueryOnly = true, Func<string, string?>? queryValue = null)
    {
        var path = Regex.Replace(o.Template, @"\{([^}]+)\}", m => Uri.EscapeDataString(pathValue(m.Groups[1].Value)));
        var q = o.Parameters.Where(p => p.In == "query" && (p.Required || !requiredQueryOnly))
            .Select(p => p.Name + "=" + Uri.EscapeDataString(queryValue?.Invoke(p.Name) ?? Sample(Deref(At(p.SchemaPointer)!))?.ToString() ?? "x"));
        var qs = string.Join('&', q);
        return "/api/v1" + path + (qs.Length > 0 ? "?" + qs : "");
    }

    /// <summary>A minimal request for the operation: headers required by the contract plus a body that validates against the schema.</summary>
    public static HttpRequestMessage Request(OperationSpec o, Func<string, string> pathValue, Func<string, string?>? queryValue = null)
    {
        var m = new HttpRequestMessage(new HttpMethod(o.Method), Url(o, pathValue, true, queryValue));
        if (o.RequiresIfMatch) { m.Headers.TryAddWithoutValidation("If-Match", "\"1\""); }
        if (o.RequiresIdempotencyKey) { m.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); }
        if (o.BodySchemaPointer is { } ptr)
        {
            var body = Sample(At(ptr)!);
            m.Content = new StringContent(body?.ToJsonString() ?? "null", System.Text.Encoding.UTF8, "application/json");
        }
        return m;
    }

    private static double? Num(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? d : null) : null;

    public static JsonNode? Sample(JsonNode schema, int depth = 0)
    {
        schema = Deref(schema);
        if (schema is not JsonObject s) { return JsonValue.Create("x"); }
        if (s["enum"] is JsonArray e && e.Count > 0) { return e[0]!.DeepClone(); }
        if (s["const"] is { } c) { return c.DeepClone(); }
        foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (s[key] is JsonArray alt && alt.Count > 0)
            {
                if (key == "allOf")
                {
                    var merged = new JsonObject();
                    foreach (var part in alt) { if (Sample(part!, depth + 1) is JsonObject po) { foreach (var (k, v) in po) { merged[k] = v?.DeepClone(); } } }
                    return merged;
                }
                return Sample(alt[0]!, depth + 1);
            }
        }
        var type = s["type"] switch
        {
            JsonArray a => a.Select(x => x?.GetValue<string>()).FirstOrDefault(t => t != "null"),
            JsonValue v => v.GetValue<string>(),
            _ => s["properties"] is not null ? "object" : null,
        };
        switch (type)
        {
            case "object":
                var o = new JsonObject();
                if (s["required"] is JsonArray req && s["properties"] is JsonObject props && depth < 8)
                {
                    foreach (var name in req.Select(r => r!.GetValue<string>())) { o[name] = props[name] is { } ps ? Sample(ps, depth + 1) : JsonValue.Create("x"); }
                }
                return o;
            case "array":
                var arr = new JsonArray();
                var min = (int)(Num(s["minItems"]) ?? 0);
                if (s["items"] is { } items && depth < 8) { for (var i = 0; i < min; i++) { arr.Add(Sample(items, depth + 1)); } }
                return arr;
            case "integer": return JsonValue.Create((long)(Num(s["minimum"]) ?? 1));
            case "number": return JsonValue.Create(Num(s["minimum"]) ?? 1d);
            case "boolean": return JsonValue.Create(true);
            default:
                var format = s["format"]?.GetValue<string>();
                if (format == "uuid") { return JsonValue.Create(Guid.NewGuid().ToString()); }
                if (format == "date") { return JsonValue.Create("2026-10-27"); }
                if (format == "date-time") { return JsonValue.Create("2026-10-27T10:00:00Z"); }
                var pattern = s["pattern"]?.GetValue<string>();
                if (pattern is not null)
                {
                    foreach (var cand in new[] { "1.00", "GBP", "ref-1", "A-1", "1", "x" }) { if (Regex.IsMatch(cand, pattern)) { return JsonValue.Create(cand); } }
                }
                var len = (int)(Num(s["minLength"]) ?? 1);
                return JsonValue.Create(new string('a', Math.Max(len, 8)));
        }
    }

}
