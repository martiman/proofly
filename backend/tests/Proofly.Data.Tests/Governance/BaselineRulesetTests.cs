using System.Text.Json;
using Proofly.Data.Governance;
using Proofly.Data.Orchestration;
using Proofly.Domain.Enums;

namespace Proofly.Data.Tests.Governance;

public class BaselineRulesetTests(SchemaFixture f) : IClassFixture<SchemaFixture>
{
    [Fact]
    public void Every_payload_passes_the_config_validator_and_the_ruleset_parameters_parse()
    {
        Assert.Empty(BaselineRuleset.Validate());
        Assert.Equal(BaselineRuleset.Entries.Count, BaselineRuleset.Entries.Select(e => e.Id).Distinct().Count());
        var snapshot = JsonSerializer.Serialize(BaselineRuleset.Entries.Where(e => e.Kind == "rule")
            .Select(e => new { id = e.Id, kind = e.Kind, name = e.Name, version = e.ConfigVersion, payload = e.Payload }));
        var o = RulesetParameters.Apply(new RunProcessingOptions(), snapshot);
        Assert.Equal(0.01m, o.Matching.AmountTolerance["GBP"]);
        Assert.Equal(0.01m, o.Matching.AmountTolerance["EUR"]);
        Assert.Equal(1m, o.Matching.AmountTolerance["JPY"]);
        Assert.Equal(0.01m, o.Ledger.Tolerance);
        Assert.Equal(Severity.Medium, o.Matching.ValueMismatchSeverity);
        Assert.Equal(Severity.High, o.Safeguarding.StaleSeverity);
    }

    [Fact]
    public async Task Install_is_served_by_settings_and_creates_the_v1_snapshot_idempotently()
    {
        await new MigrationRunner(f.DataSource).RunAsync();
        await BaselineRuleset.InstallAsync(f.DataSource);
        await BaselineRuleset.InstallAsync(f.DataSource); // idempotent

        await using var c = f.DataSource.CreateCommand("SELECT count(*), count(*) FILTER (WHERE kind='rule'), count(*) FILTER (WHERE kind='source'), count(*) FILTER (WHERE kind='approval_policy'), count(*) FILTER (WHERE kind='setting') FROM config_rules");
        await using (var r = await c.ExecuteReaderAsync())
        {
            await r.ReadAsync();
            Assert.Equal((BaselineRuleset.Entries.Count, 9L, 8L, 1L, 1L), ((int)r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        }
        await using var s = f.DataSource.CreateCommand("SELECT jsonb_array_length(snapshot), snapshot::text FROM ruleset_versions WHERE version = 'v1'");
        await using var sr = await s.ExecuteReaderAsync();
        await sr.ReadAsync();
        Assert.Equal(BaselineRuleset.Entries.Count, sr.GetInt32(0));
        using var doc = JsonDocument.Parse(sr.GetString(1));
        Assert.Contains(doc.RootElement.EnumerateArray(), e => e.GetProperty("id").GetString() == "MATCH-AMOUNT-TOLERANCE-JPY" && e.GetProperty("payload").GetProperty("tolerance").GetString() == "1 JPY");
        await sr.CloseAsync();

        // getSettings serves them: rules, one source config per data source, the approval policy and the freshness policy.
        await using (var le = f.DataSource.CreateCommand("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-S','Settings Ltd','GBP') ON CONFLICT DO NOTHING")) { await le.ExecuteNonQueryAsync(); }
        var snap = (await new SettingsRepository(f.DataSource).GetAsync("LE-S", null))!;
        Assert.Equal(9, snap.Rules.Count);
        Assert.Contains(snap.Rules, r => r.RuleId == "MATCH-AMOUNT-TOLERANCE-EUR" && r.Tolerance == "0.01 EUR" && r.Control == "matching" && r.Severity == "MEDIUM");
        Assert.Equal(8, snap.Sources.Count);
        Assert.All(snap.Sources, s => { Assert.True(s.Mandatory); Assert.Matches(@"^\d\d:\d\d$", s.ExpectedByLocalTime); });
        var policy = Assert.Single(snap.ApprovalPolicies);
        Assert.Equal(("APPROVER", "v1"), (policy.RequiredRole, policy.Version));
        Assert.Contains("09:00", snap.FreshnessPolicy);
    }
}
