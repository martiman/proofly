using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Proofly.Api.Platform.Contract;

/// <summary>
/// In-house validator for exactly the JSON Schema keywords the OpenAPI contract uses (no third-party schema library,
/// see deploy/customer/THIRD-PARTY.md). Schemas are plain JsonNode trees; local $refs resolve against the document root.
/// Unknown keywords are rejected by <see cref="AssertSupported"/> at load time, never silently ignored.
/// </summary>
public sealed class SchemaValidator
{
    /// <summary>Keywords that constrain instances and are implemented below.</summary>
    public static readonly IReadOnlySet<string> Validating = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "enum", "const", "required", "properties", "additionalProperties", "propertyNames", "items", "minItems", "maxItems", "uniqueItems",
        "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "pattern", "format",
        "$ref", "oneOf", "anyOf", "allOf", "not",
    };

    /// <summary>Keywords that carry no validation semantics. (Names starting with "x-" are extensions and also ignored.)</summary>
    public static readonly IReadOnlySet<string> Annotations = new HashSet<string>(StringComparer.Ordinal)
    {
        "description", "title", "examples", "example", "default", "deprecated", "readOnly", "writeOnly",
    };

    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal)
    {
        "date-time", "date", "email", "uuid", "uri", "uri-reference",
    };

    private static readonly ConcurrentDictionary<string, Regex> Patterns = new();
    private readonly JsonNode _root;

    public SchemaValidator(JsonNode root) => _root = root;

    public static bool IsSupportedKeyword(string k) => Validating.Contains(k) || Annotations.Contains(k) || k.StartsWith("x-", StringComparison.Ordinal);

    /// <summary>Lists unsupported keywords/formats used by every schema reachable from the given schema locations (empty = fine).</summary>
    public IReadOnlyList<string> FindUnsupported(IEnumerable<(string Location, JsonNode Schema)> schemas)
    {
        var problems = new List<string>();
        foreach (var (loc, s) in schemas) { Inspect(s, loc, problems); }
        return problems;
    }

    /// <summary>Every keyword used in a schema position (recursive), for the keyword-inventory test.</summary>
    public static void CollectKeywords(JsonNode? schema, string location, ICollection<(string Keyword, string Location)> sink)
    {
        if (schema is not JsonObject o) { return; }
        foreach (var (k, v) in o)
        {
            sink.Add((k, location));
            switch (k)
            {
                case "properties" when v is JsonObject props:
                    foreach (var (name, sub) in props) { CollectKeywords(sub, $"{location}/properties/{name}", sink); }
                    break;
                case "items" or "additionalProperties" or "propertyNames" or "not":
                    CollectKeywords(v, $"{location}/{k}", sink);
                    break;
                case "oneOf" or "anyOf" or "allOf" when v is JsonArray arr:
                    for (var i = 0; i < arr.Count; i++) { CollectKeywords(arr[i], $"{location}/{k}/{i}", sink); }
                    break;
                case "format" when v is JsonValue fv && fv.TryGetValue<string>(out var f):
                    sink.Add(("format:" + f, location));
                    break;
            }
        }
    }

    private static void Inspect(JsonNode? schema, string loc, List<string> problems)
    {
        var used = new List<(string Keyword, string Location)>();
        CollectKeywords(schema, loc, used);
        foreach (var (k, l) in used)
        {
            if (k.StartsWith("format:", StringComparison.Ordinal)) { if (!Formats.Contains(k[7..])) { problems.Add($"{l}: unsupported format '{k[7..]}'"); } }
            else if (!IsSupportedKeyword(k)) { problems.Add($"{l}: unsupported keyword '{k}'"); }
        }
    }

    /// <summary>Validates an instance against the schema at the given JSON pointer of the root document.</summary>
    public IReadOnlyList<(string Pointer, string Message)> Validate(string schemaPointer, JsonNode? instance)
    {
        var schema = Walk(schemaPointer) ?? throw new InvalidOperationException($"No schema at {schemaPointer}.");
        var errors = new List<(string, string)>();
        Check(schema, instance, "", errors, 0);
        return errors;
    }

    private JsonNode? Walk(string pointer)
    {
        JsonNode? cur = _root;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            cur = cur is JsonObject o ? o[seg] : cur is JsonArray a && int.TryParse(seg, out var ix) && ix >= 0 && ix < a.Count ? a[ix] : null;
            if (cur is null) { return null; }
        }
        return cur;
    }

    private JsonNode Resolve(JsonNode schema)
    {
        for (var i = 0; i < 32 && schema is JsonObject o && o["$ref"] is JsonValue r; i++)
        {
            var target = r.GetValue<string>();
            if (!target.StartsWith("#/", StringComparison.Ordinal)) { throw new InvalidDataException($"Only local $ref supported: {target}"); }
            schema = Walk(target[1..]) ?? throw new InvalidDataException($"Unresolvable $ref {target}.");
        }
        return schema;
    }

    private void Check(JsonNode schemaNode, JsonNode? inst, string ptr, List<(string, string)> errors, int depth)
    {
        if (depth > 64) { throw new InvalidDataException("Schema nesting too deep."); }
        if (schemaNode is JsonValue { } bv && bv.TryGetValue<bool>(out var allowAll))
        {
            if (!allowAll) { errors.Add((ptr, "No value is allowed here.")); }
            return;
        }
        var s = (JsonObject)Resolve(schemaNode);

        if (s["type"] is { } t && !TypeMatches(t, inst))
        {
            errors.Add((ptr, $"Expected {Describe(t)} but found {KindOf(inst)}."));
            return; // type-specific keywords below would be meaningless
        }
        if (s["enum"] is JsonArray en && !en.Any(e => JsonNode.DeepEquals(e, inst))) { errors.Add((ptr, "The value is not one of the allowed values.")); }
        if (s.ContainsKey("const") && !JsonNode.DeepEquals(s["const"], inst)) { errors.Add((ptr, "The value is not the required constant.")); }

        switch (inst)
        {
            case JsonObject obj: CheckObject(s, obj, ptr, errors, depth); break;
            case JsonArray arr: CheckArray(s, arr, ptr, errors, depth); break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String: CheckString(s, v.GetValue<string>(), ptr, errors); break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number: CheckNumber(s, v, ptr, errors); break;
        }

        if (s["allOf"] is JsonArray all) { foreach (var sub in all) { Check(sub!, inst, ptr, errors, depth + 1); } }
        if (s["anyOf"] is JsonArray any && !any.Any(sub => Matches(sub!, inst, depth))) { errors.Add((ptr, "The value does not match any of the allowed schemas.")); }
        if (s["oneOf"] is JsonArray one)
        {
            var hits = one.Count(sub => Matches(sub!, inst, depth));
            if (hits == 0) { ExplainNoMatch(one, inst, ptr, errors, depth); }
            else if (hits > 1) { errors.Add((ptr, "The value matches more than one of the exclusive schemas.")); }
        }
        if (s["not"] is { } not && Matches(not, inst, depth)) { errors.Add((ptr, "The value matches a schema it must not match.")); }
    }

    private bool Matches(JsonNode schema, JsonNode? inst, int depth)
    {
        var tmp = new List<(string, string)>();
        Check(schema, inst, "", tmp, depth + 1);
        return tmp.Count == 0;
    }

    /// <summary>With no matching branch, report the errors of the single branch whose type fits the instance, else one summary.</summary>
    private void ExplainNoMatch(JsonArray branches, JsonNode? inst, string ptr, List<(string, string)> errors, int depth)
    {
        var fitting = branches.Where(b => Resolve(b!) is JsonObject bo && (bo["type"] is not { } bt || TypeMatches(bt, inst))).ToList();
        if (fitting.Count == 1) { Check(fitting[0]!, inst, ptr, errors, depth + 1); }
        if (errors.Count == 0 || fitting.Count != 1) { errors.Add((ptr, "The value does not match any of the allowed schemas.")); }
    }

    private void CheckObject(JsonObject s, JsonObject obj, string ptr, List<(string, string)> errors, int depth)
    {
        if (s["required"] is JsonArray req)
        {
            foreach (var r in req)
            {
                var name = r!.GetValue<string>();
                if (!obj.ContainsKey(name)) { errors.Add((Child(ptr, name), "Required property is missing.")); }
            }
        }
        if (s["propertyNames"] is { } names)
        {
            foreach (var (name, _) in obj)
            {
                var keyErrors = new List<(string, string)>();
                Check(names, JsonValue.Create(name), "", keyErrors, depth + 1);
                if (keyErrors.Count > 0) { errors.Add((Child(ptr, name), "The property name is not allowed: " + keyErrors[0].Item2)); }
            }
        }
        var props = s["properties"] as JsonObject;
        foreach (var (name, value) in obj)
        {
            if (props is not null && props.TryGetPropertyValue(name, out var sub)) { Check(sub!, value, Child(ptr, name), errors, depth + 1); }
            else if (s["additionalProperties"] is { } ap) { Check(ap, value, Child(ptr, name), errors, depth + 1); }
        }
    }

    private void CheckArray(JsonObject s, JsonArray arr, string ptr, List<(string, string)> errors, int depth)
    {
        if (Int(s["minItems"]) is { } min && arr.Count < min) { errors.Add((ptr, $"The array must have at least {min} item(s).")); }
        if (Int(s["maxItems"]) is { } max && arr.Count > max) { errors.Add((ptr, $"The array must have at most {max} item(s).")); }
        if (s["uniqueItems"] is JsonValue u && u.TryGetValue<bool>(out var unique) && unique)
        {
            for (var i = 1; i < arr.Count; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    if (JsonNode.DeepEquals(arr[i], arr[j])) { errors.Add((Child(ptr, i.ToString(CultureInfo.InvariantCulture)), "Array items must be unique.")); break; }
                }
            }
        }
        if (s["items"] is { } items)
        {
            for (var i = 0; i < arr.Count; i++) { Check(items, arr[i], Child(ptr, i.ToString(CultureInfo.InvariantCulture)), errors, depth + 1); }
        }
    }

    private static void CheckString(JsonObject s, string v, string ptr, List<(string, string)> errors)
    {
        if (s["minLength"] is not null || s["maxLength"] is not null)
        {
            var len = v.EnumerateRunes().Count();
            if (Int(s["minLength"]) is { } mn && len < mn) { errors.Add((ptr, $"The string must be at least {mn} character(s).")); }
            if (Int(s["maxLength"]) is { } mx && len > mx) { errors.Add((ptr, $"The string must be at most {mx} character(s).")); }
        }
        if (s["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var pattern) && !PatternFor(pattern).IsMatch(v)) { errors.Add((ptr, $"The string does not match the pattern {pattern}.")); }
        if (s["format"] is JsonValue fv && fv.TryGetValue<string>(out var format) && !FormatOk(format, v)) { errors.Add((ptr, $"The string is not a valid {format}.")); }
    }

    private static void CheckNumber(JsonObject s, JsonValue v, string ptr, List<(string, string)> errors)
    {
        var n = ToDecimal(v);
        if (n is null) { return; }
        if (Num(s["minimum"]) is { } mn && n < mn) { errors.Add((ptr, $"The value must be at least {mn}.")); }
        if (Num(s["maximum"]) is { } mx && n > mx) { errors.Add((ptr, $"The value must be at most {mx}.")); }
        if (Num(s["exclusiveMinimum"]) is { } emn && n <= emn) { errors.Add((ptr, $"The value must be greater than {emn}.")); }
        if (Num(s["exclusiveMaximum"]) is { } emx && n >= emx) { errors.Add((ptr, $"The value must be less than {emx}.")); }
    }

    private static decimal? ToDecimal(JsonNode? n) =>
        n is not null && decimal.TryParse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? ToDecimal(v) : null;
    private static int? Int(JsonNode? n) => Num(n) is { } d ? (int)d : null;

    private static Regex PatternFor(string pattern) => Patterns.GetOrAdd(pattern, p =>
    {
        // JSON Schema patterns are ECMA 262: "$" matches only at the very end and \d is ASCII; .NET differs on both.
        var adjusted = p.EndsWith('$') && !p.EndsWith("\\$", StringComparison.Ordinal) ? p[..^1] + "\\z" : p;
        adjusted = adjusted.Replace("\\d", "[0-9]", StringComparison.Ordinal);
        return new Regex(adjusted, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    });

    private static bool FormatOk(string format, string v) => format switch
    {
        "date-time" => Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "date" => Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant)
            && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "uuid" => v.Length == 36 && Guid.TryParseExact(v, "D", out _),
        "email" => !v.Any(char.IsWhiteSpace) && MailAddress.TryCreate(v, out var m) && m.Address == v && v.Contains('@') && m.Host.Contains('.'),
        "uri" => Uri.TryCreate(v, UriKind.Absolute, out _) && !v.Any(char.IsWhiteSpace),
        "uri-reference" => !v.Any(char.IsWhiteSpace) && Uri.TryCreate(v, UriKind.RelativeOrAbsolute, out _),
        _ => throw new InvalidDataException($"Unsupported format {format}."),
    };

    private static bool TypeMatches(JsonNode type, JsonNode? inst) => type switch
    {
        JsonArray a => a.Any(x => x is not null && TypeMatches(x, inst)),
        JsonValue v when v.TryGetValue<string>(out var name) => Is(name, inst),
        _ => throw new InvalidDataException("Invalid type keyword."),
    };

    private static bool Is(string name, JsonNode? inst) => name switch
    {
        "null" => inst is null,
        "object" => inst is JsonObject,
        "array" => inst is JsonArray,
        "string" => inst is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => inst is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => inst is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => inst is JsonValue v && v.GetValueKind() == JsonValueKind.Number && ToDecimal(v) is { } d && d == decimal.Truncate(d),
        _ => throw new InvalidDataException($"Unsupported type {name}."),
    };

    private static string Describe(JsonNode type) => type is JsonArray a ? string.Join(" or ", a.Select(x => x?.ToString())) : type.ToString();

    private static string KindOf(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind().ToString().ToLowerInvariant() is "true" or "false" ? "boolean" : v.GetValueKind().ToString().ToLowerInvariant(),
        _ => "value",
    };

    private static string Child(string ptr, string seg) => ptr + "/" + seg.Replace("~", "~0").Replace("/", "~1");
}
