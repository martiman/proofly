using System.Text.Json.Nodes;
using Proofly.Api.Platform.Contract;

namespace Proofly.Contract.Tests;

/// <summary>
/// The platform validates requests and responses with the in-house SchemaValidator. This inventory fails as soon as
/// openapi.yaml starts to use a JSON Schema keyword or format the validator does not implement.
/// </summary>
public sealed class SchemaKeywordInventoryTests
{
    private static List<(string Keyword, string Location)> Used()
    {
        var sink = new List<(string, string)>();
        var root = ContractDoc.Root;
        foreach (var (n, s) in (JsonObject)root["components"]!["schemas"]!) { SchemaValidator.CollectKeywords(s, $"/components/schemas/{n}", sink); }
        foreach (var (n, p) in (JsonObject?)root["components"]!["parameters"] ?? new JsonObject()) { SchemaValidator.CollectKeywords(p!["schema"], $"/components/parameters/{n}", sink); }
        foreach (var (path, item) in (JsonObject)root["paths"]!)
        {
            foreach (var (m, opNode) in (JsonObject)item!)
            {
                if (opNode is not JsonObject op) { continue; }
                foreach (var p in op["parameters"] as JsonArray ?? []) { SchemaValidator.CollectKeywords(p!["schema"], $"{path}/{m}/parameters", sink); }
                SchemaValidator.CollectKeywords(op["requestBody"]?["content"]?["application/json"]?["schema"], $"{path}/{m}/requestBody", sink);
                foreach (var (code, r) in op["responses"] as JsonObject ?? [])
                {
                    SchemaValidator.CollectKeywords(r?["content"]?["application/json"]?["schema"], $"{path}/{m}/responses/{code}", sink);
                }
            }
        }
        return sink;
    }

    [Fact]
    public void Contract_uses_only_keywords_and_formats_the_validator_implements()
    {
        var bad = Used().Where(u => u.Keyword.StartsWith("format:") ? !SchemaValidator.Formats.Contains(u.Keyword[7..]) : !SchemaValidator.IsSupportedKeyword(u.Keyword)).ToList();
        Assert.True(bad.Count == 0, "Unsupported: " + string.Join("; ", bad.Select(b => $"{b.Keyword} at {b.Location}")));
    }

    [Fact]
    public void Inventory_is_not_vacuous()
    {
        var keywords = Used().Select(u => u.Keyword).ToHashSet();
        foreach (var k in new[] { "type", "properties", "required", "$ref", "enum", "items", "pattern", "format:date-time" }) { Assert.Contains(k, keywords); }
        Console.WriteLine("KEYWORDS: " + string.Join(", ", keywords.OrderBy(x => x)));
    }

    [Theory]
    [InlineData("nullable: true")]
    [InlineData("multipleOf: 2")]
    [InlineData("patternProperties: {}")]
    [InlineData("format: hostname")]
    public void Unsupported_keyword_is_refused_at_load(string extra)
    {
        var yaml = $$"""
            openapi: 3.1.0
            servers: [{url: 'http://x/api'}]
            paths:
              /a:
                get:
                  operationId: a
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema: {type: string, {{extra}}}
            """;
        Assert.Throws<InvalidDataException>(() => new OpenApiDocument(yaml, "https://t/x.yaml"));
    }
}
