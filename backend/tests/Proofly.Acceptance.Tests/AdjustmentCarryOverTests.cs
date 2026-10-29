using Proofly.Acceptance.Tests.Support;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// Safeguarding adjustments belong to (legal entity, business date), not to one run version: an approved, unexpired adjustment stays valid in every later
/// version, and the position of the new version includes it. A superseded version keeps the snapshot it was calculated with.
/// </summary>
public sealed class AdjustmentCarryOverTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    private Task<string> Snapshot(string run) => _w.Snapshot(
        ("exceptions", $"run_id='{run}'"), ("approvals", $"run_id='{run}'"), ("safeguarding_positions", $"run_id='{run}'"), ("safeguarding_adjustments", $"run_id='{run}'"),
        ("match_groups", $"run_id='{run}'"), ("run_control_results", $"run_id='{run}'"), ("audit_events", $"run_id='{run}' AND event_type NOT IN ('RUN_REPROCESS_REQUESTED','RUN_STARTED')"));

    private async Task Reprocess(string run)
    {
        (await _w.Send("approver1", HttpMethod.Post, $"/api/v1/runs/{run}/reprocess", new { reason = "Include the approved adjustment" }, await _w.ETag("approver1", $"/api/v1/runs/{run}"))).Is(202);
        await _w.Drain();
    }

    private Task<decimal> Approved(string run, string ccy) =>
        _w.Scalar<decimal>($"SELECT (detail->>'approvedAdjustments')::numeric FROM safeguarding_positions WHERE run_id='{run}' AND currency='{ccy}'");

    [Fact]
    public async Task Approved_adjustment_is_carried_into_the_reprocessed_version_and_the_old_snapshot_stays()
    {
        await _w.Users();
        var v1 = await _w.ProcessedRun(Flow.Le);
        var hashBefore = await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{v1}'");
        Assert.Equal(0m, await Approved(v1, "GBP"));

        var adj = (await _w.ProposeAdjustment("analyst1", v1, amount: "100.00")).Is(201).Str("id");
        (await _w.DecideAdjustment("approver1", adj)).Is(200);
        Assert.True(await _w.Scalar<bool>($"SELECT stale FROM safeguarding_positions WHERE run_id='{v1}' AND currency='GBP'")); // v1 is flagged, never recomputed
        var v1Snapshot = await Snapshot(v1);

        await Reprocess(v1);
        var v2 = Flow.RunV2;

        // v2 includes the adjustment that was created in v1; the provenance column still names v1.
        Assert.Equal(100m, await Approved(v2, "GBP"));
        Assert.Equal(v1, await _w.Scalar<string>($"SELECT run_id FROM safeguarding_adjustments WHERE id='{adj}'"));
        Assert.False(await _w.Scalar<bool>($"SELECT stale FROM safeguarding_positions WHERE run_id='{v2}' AND currency='GBP'"));
        var api = await _w.Get("viewer1", $"/api/v1/runs/{v2}/safeguarding");
        api.Is(200);
        var gbp = api["positions"].EnumerateArray().Single(p => p.GetProperty("currency").GetString() == "GBP");
        Assert.Equal("100.00", gbp.GetProperty("approvedAdjustments").GetProperty("amount").GetString());
        var listed = await _w.Get("viewer1", $"/api/v1/runs/{v2}/safeguarding/adjustments");
        listed.Is(200);
        Assert.Contains(listed.Items(), a => a.GetProperty("id").GetString() == adj);

        // v1 keeps what it was calculated with: unchanged rows and a zero adjustment in its own snapshot.
        Assert.Equal(v1Snapshot, await Snapshot(v1));
        Assert.Equal(0m, await Approved(v1, "GBP"));
        Assert.Equal(hashBefore, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{v1}'"));
        var v1Api = await _w.Get("viewer1", $"/api/v1/runs/{v1}/safeguarding");
        Assert.Equal("0.00", v1Api["positions"].EnumerateArray().Single(p => p.GetProperty("currency").GetString() == "GBP").GetProperty("approvedAdjustments").GetProperty("amount").GetString());

        // The result differs only by the adjustment: same findings outside safeguarding, same positions in the other currencies, GBP moves by exactly 100.00.
        Assert.NotEqual(hashBefore, await _w.Scalar<string>($"SELECT result_hash FROM runs WHERE id='{v2}'"));
        Assert.Equal(
            await _w.Strings($"SELECT finding_key FROM exceptions WHERE run_id='{v1}' AND control <> 'safeguarding' ORDER BY finding_key"),
            await _w.Strings($"SELECT finding_key FROM exceptions WHERE run_id='{v2}' AND control <> 'safeguarding' ORDER BY finding_key"));
        Assert.Equal(
            await _w.Strings($"SELECT currency || ':' || obligation_amount || ':' || eligible_amount || ':' || COALESCE(difference_amount::text,'-') || ':' || state FROM safeguarding_positions WHERE run_id='{v1}' AND currency <> 'GBP' ORDER BY currency"),
            await _w.Strings($"SELECT currency || ':' || obligation_amount || ':' || eligible_amount || ':' || COALESCE(difference_amount::text,'-') || ':' || state FROM safeguarding_positions WHERE run_id='{v2}' AND currency <> 'GBP' ORDER BY currency"));
        var d1 = await _w.Scalar<decimal>($"SELECT difference_amount FROM safeguarding_positions WHERE run_id='{v1}' AND currency='GBP'");
        var d2 = await _w.Scalar<decimal>($"SELECT difference_amount FROM safeguarding_positions WHERE run_id='{v2}' AND currency='GBP'");
        Assert.Equal(100m, d2 - d1);
        Assert.True((await _w.VerifyAudit()).IsValid);
    }

    [Fact]
    public async Task Proposed_adjustment_of_a_superseded_version_can_still_be_decided_and_flags_the_current_version()
    {
        await _w.Users();
        var v1 = await _w.ProcessedRun(Flow.Le);
        var adj = (await _w.ProposeAdjustment("analyst1", v1, amount: "40.00")).Is(201).Str("id");
        await Reprocess(v1);
        Assert.Equal(0m, await Approved(Flow.RunV2, "GBP")); // proposed adjustments are not counted

        (await _w.DecideAdjustment("approver1", adj)).Is(200);
        Assert.True(await _w.Scalar<bool>($"SELECT stale FROM safeguarding_positions WHERE run_id='{Flow.RunV2}' AND currency='GBP'"));
        await Reprocess(Flow.RunV2);
        Assert.Equal(40m, await Approved("RUN-" + Flow.Le + "-20261027-v3", "GBP"));
    }
}
