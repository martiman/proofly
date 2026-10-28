using System.Text.Json.Nodes;
using Proofly.Data.Governance.Commands;

namespace Proofly.Data.Tests.Governance;

public class ConfigPayloadValidatorTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;
    private const string Rule = """{"control":"matching","description":"d","tolerance":"0.01 GBP","severity":"HIGH","effectiveFrom":"2026-01-01"}""";

    [Fact]
    public void Valid_rule_is_normalised_and_echoes_are_dropped()
    {
        var o = Obj(Rule); o["ruleId"] = "R1"; o["version"] = "v3"; o["pendingChangeId"] = null;
        var (v, e) = ConfigPayloadValidator.Validate("RULE", "R1", o);
        Assert.Empty(e);
        Assert.Equal(["control", "description", "effectiveFrom", "severity", "tolerance"], v!.Select(p => p.Key).Order().ToArray());
    }

    [Theory]
    [InlineData("""{"control":"matching","description":"d","tolerance":"-1 GBP","severity":"HIGH","effectiveFrom":"2026-01-01"}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":"1.5","severity":"HIGH","effectiveFrom":"2026-01-01"}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":"1 gbp","severity":"HIGH","effectiveFrom":"2026-01-01"}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":"1 GBP","severity":"HIGH","effectiveFrom":"01/01/2026"}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":"1 GBP","severity":"HIGH","effectiveFrom":"2026-01-01","extra":true}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":"1 GBP","severity":"HIGH"}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":1,"severity":"HIGH","effectiveFrom":"2026-01-01"}""")]
    [InlineData("""{"control":"matching","description":"d","tolerance":"1 GBP","severity":"high","effectiveFrom":"2026-01-01"}""")]
    public void Invalid_rules_are_rejected(string json) => Assert.NotEmpty(ConfigPayloadValidator.Validate("RULE", "R1", Obj(json)).Errors);

    [Fact]
    public void Echo_of_a_different_rule_id_is_rejected()
    {
        var o = Obj(Rule); o["ruleId"] = "OTHER";
        Assert.NotEmpty(ConfigPayloadValidator.Validate("RULE", "R1", o).Errors);
    }

    [Fact]
    public void User_access_sorts_and_rejects_duplicates_and_unknown_roles()
    {
        var (v, e) = ConfigPayloadValidator.Validate("USER_ACCESS", "u", Obj("""{"roles":["VIEWER","ANALYST"],"legalEntityIds":["LE2","LE1"]}"""));
        Assert.Empty(e);
        Assert.Equal("""["ANALYST","VIEWER"]""", v!["roles"]!.ToJsonString());
        Assert.NotEmpty(ConfigPayloadValidator.Validate("USER_ACCESS", "u", Obj("""{"roles":["ANALYST","ANALYST"],"legalEntityIds":["LE1"]}""")).Errors);
        Assert.NotEmpty(ConfigPayloadValidator.Validate("USER_ACCESS", "u", Obj("""{"roles":["ROOT"],"legalEntityIds":["LE1"]}""")).Errors);
        Assert.NotEmpty(ConfigPayloadValidator.Validate("USER_ACCESS", "u", Obj("""{"roles":[],"legalEntityIds":[]}""")).Errors);
    }

    [Fact]
    public void Diff_lists_only_changed_keys()
    {
        var d = ConfigPayloadValidator.Diff(Obj("""{"a":"1","b":"2"}"""), Obj("""{"a":"1","b":"3","c":true}"""));
        Assert.Equal(["b", "c"], d.Select(x => x.Path).ToArray());
    }
}
