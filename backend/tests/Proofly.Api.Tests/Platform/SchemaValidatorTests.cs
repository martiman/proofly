using System.Text.Json.Nodes;
using Proofly.Api.Platform.Contract;

namespace Proofly.Api.Tests.Platform;

public sealed class SchemaValidatorTests
{
    private static readonly SchemaValidator V = new(JsonNode.Parse("""
    {"components":{"schemas":{
      "Money":{"type":"object","required":["amount","currency"],"additionalProperties":false,"properties":{
        "amount":{"type":"string","pattern":"^-?\\d+(\\.\\d{1,6})?$"},"currency":{"type":"string","pattern":"^[A-Z]{3}$"}}},
      "Ref":{"$ref":"#/components/schemas/Money"},
      "NullableMoney":{"oneOf":[{"$ref":"#/components/schemas/Money"},{"type":"null"}]},
      "Str":{"type":"string","minLength":2,"maxLength":4},
      "Int":{"type":"integer","minimum":1,"maximum":200},
      "Excl":{"type":"number","exclusiveMinimum":0,"exclusiveMaximum":1},
      "Arr":{"type":"array","minItems":1,"maxItems":2,"uniqueItems":true,"items":{"type":"string","enum":["a","b","c"]}},
      "Multi":{"type":["string","null"],"format":"date-time"},
      "Date":{"type":"string","format":"date"},
      "Uuid":{"type":"string","format":"uuid"},
      "Email":{"type":"string","format":"email"},
      "Uri":{"type":"string","format":"uri-reference"},
      "Const":{"type":"boolean","const":true},
      "Any":{"anyOf":[{"type":"integer"},{"type":"string","minLength":3}]},
      "All":{"allOf":[{"type":"object","required":["a"]},{"type":"object","required":["b"]}]},
      "Not":{"not":{"type":"string"}},
      "Names":{"type":"object","propertyNames":{"enum":["a","b"]},"additionalProperties":{"type":"integer"}},
      "Both":{"oneOf":[{"type":"integer"},{"type":"number"}]}
    }}}
    """)!);

    private static IReadOnlyList<(string Pointer, string Message)> Run(string schema, string json) =>
        V.Validate("/components/schemas/" + schema, JsonNode.Parse(json));

    [Theory]
    [InlineData("Money", """{"amount":"-50.00","currency":"EUR"}""")]
    [InlineData("Ref", """{"amount":"1","currency":"EUR"}""")]
    [InlineData("NullableMoney", "null")]
    [InlineData("NullableMoney", """{"amount":"1","currency":"EUR"}""")]
    [InlineData("Str", "\"abc\"")]
    [InlineData("Int", "1")]
    [InlineData("Int", "200")]
    [InlineData("Int", "5.0")]
    [InlineData("Excl", "0.5")]
    [InlineData("Arr", """["a","b"]""")]
    [InlineData("Multi", "null")]
    [InlineData("Multi", "\"2026-10-27T10:00:00Z\"")]
    [InlineData("Multi", "\"2026-10-27T10:00:00.123+02:00\"")]
    [InlineData("Date", "\"2026-10-27\"")]
    [InlineData("Uuid", "\"3f2504e0-4f89-41d3-9a0c-0305e82c3301\"")]
    [InlineData("Email", "\"a@b.cz\"")]
    [InlineData("Uri", "\"https://proofly.example/problems/x\"")]
    [InlineData("Uri", "\"/relative/path\"")]
    [InlineData("Const", "true")]
    [InlineData("Any", "3")]
    [InlineData("Any", "\"abc\"")]
    [InlineData("All", """{"a":1,"b":2}""")]
    [InlineData("Not", "1")]
    [InlineData("Names", """{"a":1,"b":2}""")]
    public void Valid_instances_pass(string schema, string json) => Assert.Empty(Run(schema, json));

    [Theory]
    [InlineData("Money", """{"amount":-50.0,"currency":"EUR"}""")]
    [InlineData("Money", """{"amount":"1.1234567","currency":"EUR"}""")]
    [InlineData("Money", """{"amount":"1.00","currency":"eur"}""")]
    [InlineData("Money", """{"amount":"1.00","currency":"EUR\n"}""")]
    [InlineData("Money", """{"amount":"1.00"}""")]
    [InlineData("Money", """{"amount":"1.00","currency":"EUR","extra":1}""")]
    [InlineData("Money", "[]")]
    [InlineData("Money", "null")]
    [InlineData("NullableMoney", "1")]
    [InlineData("NullableMoney", """{"amount":"x","currency":"EUR"}""")]
    [InlineData("Str", "\"a\"")]
    [InlineData("Str", "\"abcde\"")]
    [InlineData("Str", "1")]
    [InlineData("Int", "0")]
    [InlineData("Int", "201")]
    [InlineData("Int", "1.5")]
    [InlineData("Int", "\"1\"")]
    [InlineData("Excl", "0")]
    [InlineData("Excl", "1")]
    [InlineData("Arr", "[]")]
    [InlineData("Arr", """["a","b","c"]""")]
    [InlineData("Arr", """["a","a"]""")]
    [InlineData("Arr", """["z"]""")]
    [InlineData("Multi", "\"2026-10-27\"")]
    [InlineData("Multi", "\"2026-10-27 10:00:00Z\"")]
    [InlineData("Multi", "\"2026-10-27T10:00:00\"")]
    [InlineData("Multi", "1")]
    [InlineData("Date", "\"2026-02-30\"")]
    [InlineData("Date", "\"27.10.2026\"")]
    [InlineData("Uuid", "\"not-a-uuid\"")]
    [InlineData("Email", "\"nope\"")]
    [InlineData("Uri", "\"has space\"")]
    [InlineData("Const", "false")]
    [InlineData("Any", "\"ab\"")]
    [InlineData("All", """{"a":1}""")]
    [InlineData("Not", "\"s\"")]
    [InlineData("Both", "1")]
    [InlineData("Names", """{"c":1}""")]
    [InlineData("Names", """{"a":"x"}""")]
    public void Invalid_instances_fail(string schema, string json) => Assert.NotEmpty(Run(schema, json));

    [Fact]
    public void Errors_carry_json_pointers_with_escaping()
    {
        var e = Run("Money", """{"amount":"x","currency":"EUR"}""");
        Assert.Equal("/amount", Assert.Single(e).Pointer);
        Assert.Equal("/amount", Assert.Single(Run("Money", """{"currency":"EUR"}""")).Pointer);
        Assert.Equal("/1", Assert.Single(Run("Arr", """["a","z"]""")).Pointer);
        Assert.Equal("/extra", Assert.Single(Run("Money", """{"amount":"1","currency":"EUR","extra":1}""")).Pointer);
        Assert.Equal("", Assert.Single(Run("Money", "[]")).Pointer);
        Assert.Equal("/a~1b", Assert.Single(Run("Money", """{"amount":"1","currency":"EUR","a/b":1}""")).Pointer);
    }

    [Fact]
    public void Nullable_oneOf_reports_the_branch_that_fits_the_instance_kind()
    {
        var e = Run("NullableMoney", """{"amount":"x","currency":"EUR"}""");
        Assert.Equal("/amount", Assert.Single(e).Pointer);
    }

    [Fact]
    public void Unsupported_type_throws() =>
        Assert.Throws<InvalidDataException>(() => new SchemaValidator(JsonNode.Parse("""{"s":{"type":"bogus"}}""")!).Validate("/s", JsonNode.Parse("1")));

    [Fact]
    public void Unresolvable_ref_throws() =>
        Assert.Throws<InvalidDataException>(() => new SchemaValidator(JsonNode.Parse("""{"s":{"$ref":"#/nope"}}""")!).Validate("/s", JsonNode.Parse("1")));
}
