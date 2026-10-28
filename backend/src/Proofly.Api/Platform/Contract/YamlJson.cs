using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Proofly.Api.Platform.Contract;

/// <summary>Converts a YAML document (the OpenAPI file) to a JsonNode tree using YAML 1.2 core-schema scalar rules.</summary>
internal static partial class YamlJson
{
    public static JsonNode? Parse(TextReader reader)
    {
        var stream = new YamlStream();
        stream.Load(reader);
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode m => ToObject(m),
        YamlSequenceNode s => ToArray(s),
        YamlScalarNode sc => ToScalar(sc),
        _ => throw new InvalidDataException("Unsupported YAML node."),
    };

    private static JsonObject ToObject(YamlMappingNode m)
    {
        var o = new JsonObject();
        foreach (var (k, v) in m.Children)
        {
            var key = ((YamlScalarNode)k).Value ?? "";
            if (key == "<<") { throw new InvalidDataException("YAML merge keys are not supported in the contract."); }
            o[key] = Convert(v); // aliases share a node in YamlDotNet; Convert builds a fresh tree each time
        }
        return o;
    }

    private static JsonArray ToArray(YamlSequenceNode s)
    {
        var a = new JsonArray();
        foreach (var c in s.Children) { a.Add(Convert(c)); }
        return a;
    }

    private static JsonNode? ToScalar(YamlScalarNode sc)
    {
        var v = sc.Value;
        if (sc.Style != YamlDotNet.Core.ScalarStyle.Plain) { return JsonValue.Create(v ?? ""); }
        if (string.IsNullOrEmpty(v) || v is "null" or "Null" or "NULL" or "~") { return null; }
        if (v is "true" or "True" or "TRUE") { return JsonValue.Create(true); }
        if (v is "false" or "False" or "FALSE") { return JsonValue.Create(false); }
        if (IntRegex().IsMatch(v) && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) { return JsonValue.Create(l); }
        if (FloatRegex().IsMatch(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { return JsonValue.Create(d); }
        return JsonValue.Create(v);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")] private static partial Regex IntRegex();
    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")] private static partial Regex FloatRegex();
}
