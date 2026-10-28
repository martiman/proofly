using Proofly.Acceptance.Tests.Support;
using Proofly.Data.Governance.Commands;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// A configuration change is proposed by one administrator and activated by another, and applies to later runs only: a run that was
/// started before the activation keeps its ruleset version and its result.
/// </summary>
public sealed class ConfigChangeTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    private async Task<string> Tolerance() => (await _w.Scalar<string>($"SELECT payload->>'tolerance' FROM config_rules WHERE id='{Flow.RuleId}'"))!;

    [Fact]
    public async Task Activation_applies_from_the_effective_date_and_never_changes_a_run_started_before_it()
    {
        await _w.Users();
        await _w.SeedRule();
        var run = await _w.ProcessedRun(Flow.Le);
        var runTables = new[] { ("runs", $"id='{run}'"), ("exceptions", $"run_id='{run}'"), ("run_control_results", $"run_id='{run}'"), ("safeguarding_positions", $"run_id='{run}'"), ("match_groups", $"run_id='{run}'") };
        var runBefore = await _w.Snapshot(runTables);
        var rulesetOfRun = await _w.Scalar<string>($"SELECT ruleset_version FROM runs WHERE id='{run}'");
        var resolver = new RulesetResolver(_w.Db);
        Assert.Equal("v1", (await resolver.ResolveAsync(DateTimeOffset.UtcNow)).Version);

        // Proposed: nothing is active yet, nothing about any run or the configuration moves.
        var change = (await _w.ProposeConfig("cfg1", Flow.Tomorrow, "0.05 GBP")).Is(201);
        var id = change.Str("id");
        Assert.Equal("PENDING_APPROVAL", change.Str("status"));
        Assert.Equal("0.01 GBP", await Tolerance());
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM ruleset_versions"));
        Assert.Equal(runBefore, await _w.Snapshot(runTables));

        // Activated by the second administrator, effective tomorrow.
        var activated = (await _w.DecideConfig("cfg2", id)).Is(200);
        Assert.Equal("ACTIVE", activated.Str("status"));
        Assert.Equal(Flow.Tomorrow, activated.Str("effectiveFrom"));
        var activatedAt = await _w.Scalar<DateTime>("SELECT activated_at FROM ruleset_versions WHERE version='v2'");
        Assert.Equal(id, await _w.Scalar<string>("SELECT change_id FROM ruleset_versions WHERE version='v2'"));
        Assert.Equal("0.05 GBP", await Tolerance());

        // Not effective today: the version in force now is still the old one; from tomorrow it is the new one; never retroactive.
        Assert.Equal("v1", (await resolver.ResolveAsync(DateTimeOffset.UtcNow)).Version);
        Assert.Equal("v2", (await resolver.ResolveAsync(DateTimeOffset.UtcNow.AddDays(1).AddHours(1))).Version);
        Assert.Equal("v1", (await resolver.ResolveAsync(new DateTimeOffset(activatedAt, TimeSpan.Zero).AddMinutes(-1))).Version);

        // The run that existed before activation is exactly what it was.
        Assert.Equal(runBefore, await _w.Snapshot(runTables));
        Assert.Equal(rulesetOfRun, await _w.Scalar<string>($"SELECT ruleset_version FROM runs WHERE id='{run}'"));
        Assert.True((await _w.VerifyAudit()).IsValid);
    }

    [Fact]
    public async Task Activation_effective_today_is_not_retroactive()
    {
        await _w.Users();
        await _w.SeedRule();
        var before = DateTimeOffset.UtcNow;
        var id = (await _w.ProposeConfig("cfg1", Flow.Today)).Is(201).Str("id");
        (await _w.DecideConfig("cfg2", id)).Is(200);
        var resolver = new RulesetResolver(_w.Db);
        Assert.Equal("v2", (await resolver.ResolveAsync(DateTimeOffset.UtcNow)).Version);
        Assert.Equal("v1", (await resolver.ResolveAsync(before.AddMinutes(-1))).Version); // work started before the activation keeps the old version
    }

    [Fact]
    public async Task Rejected_change_changes_nothing_and_invalid_proposals_are_refused()
    {
        await _w.Users();
        await _w.SeedRule();
        var id = (await _w.ProposeConfig("cfg1", Flow.Tomorrow)).Is(201).Str("id");

        // A second pending change for the same target is refused; so is a past effective date, an identical object and a user without the draft permission.
        Assert.Equal("duplicate", (await _w.ProposeConfig("cfg2", Flow.Tomorrow, "0.07 GBP")).ProblemType);
        Assert.Equal(422, (await _w.ProposeConfig("cfg2", DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), "0.07 GBP")).Code);
        (await _w.ProposeConfig("analyst1", Flow.Tomorrow, "0.07 GBP")).Is(403);
        (await _w.ProposeConfig("approver1", Flow.Tomorrow, "0.07 GBP")).Is(403);
        (await _w.DecideConfig("approver1", id)).Is(403); // the approver role is not the configuration approver

        var rejected = (await _w.DecideConfig("cfg2", id, approve: false)).Is(200);
        Assert.Equal("REJECTED", rejected.Str("status"));
        Assert.Equal("0.01 GBP", await Tolerance());
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM ruleset_versions"));
        // A decided change cannot be decided again.
        var again = await _w.Send("cfg2", HttpMethod.Post, $"/api/v1/settings/changes/{id}/decision", new { decision = "APPROVE", reason = "Changed my mind later" }, await _w.ETag("cfg2", $"/api/v1/settings/changes/{id}"));
        Assert.Equal("invalid-state", again.ProblemType);
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM ruleset_versions"));
    }

    /// <summary>
    /// The first half of the rule, "a run keeps the version it started with", is tested above; the second half: the next run records the
    /// ruleset version that was in force when it started, i.e. the activated one.
    /// </summary>
    [Fact]
    public async Task Next_run_after_activation_records_the_activated_ruleset_version()
    {
        await _w.Users();
        await _w.SeedRule();
        var run = await _w.ProcessedRun(Flow.Le);
        var codeVersion = new Proofly.Data.Orchestration.RunProcessingOptions().RulesetVersion;
        Assert.Equal($"v1+{codeVersion}", await _w.Scalar<string>($"SELECT ruleset_version FROM runs WHERE id='{run}'"));
        var hash = await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'");

        var id = (await _w.ProposeConfig("cfg1", Flow.Today)).Is(201).Str("id");
        (await _w.DecideConfig("cfg2", id)).Is(200);
        Assert.Equal("v2", (await new RulesetResolver(_w.Db).ResolveAsync(DateTimeOffset.UtcNow)).Version);

        // The next run (a reprocess of the same evidence) starts under v2; the earlier one is untouched.
        (await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Re-run under the activated tolerance" },
            await _w.ETag("approver1", $"/api/v1/runs/{run}"))).Is(202);
        await _w.Drain();
        Assert.Equal($"v2+{codeVersion}", await _w.Scalar<string>($"SELECT ruleset_version FROM runs WHERE id='{Flow.RunV2}'"));
        Assert.Equal($"v1+{codeVersion}", await _w.Scalar<string>($"SELECT ruleset_version FROM runs WHERE id='{run}'"));
        Assert.Equal(hash, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{run}'"));
        Assert.True((await _w.VerifyAudit()).IsValid);
    }
}
