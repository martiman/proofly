using Npgsql;
using Proofly.Acceptance.Tests.Support;

namespace Proofly.Acceptance.Tests;

/// <summary>
/// Scenario 3 of the acceptance plan: a proposer cannot approve their own restricted action. Every kind is refused by the API
/// (403 separation-of-duties, nothing changes, no decision audit event) and, independently, by a database CHECK constraint.
/// Each test uses a user who holds BOTH the propose and the approve permission, so only the same-person rule can refuse.
/// </summary>
public sealed class SeparationOfDutiesTests : IAsyncLifetime
{
    private readonly World _w = new();
    public Task InitializeAsync() => _w.InitializeAsync();
    public Task DisposeAsync() => _w.DisposeAsync();

    /// <summary>Runs the statement in a transaction that is always rolled back; returns the database error, or null when the statement was accepted.</summary>
    private async Task<PostgresException?> TryInRolledBackTx(string sql)
    {
        await using var c = await _w.Db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex) { return ex; }
        finally { await tx.RollbackAsync(); }
    }

    private async Task AssertDbRefusesSelfApproval(string table, string setSameUser, string where, string otherUserSql)
    {
        var self = await TryInRolledBackTx($"UPDATE {table} SET {setSameUser} WHERE {where}");
        Assert.NotNull(self);
        Assert.Equal(PostgresErrorCodes.CheckViolation, self!.SqlState);
        Assert.Equal(table, self.TableName);
        // Control: the same statement with a different person is accepted, so the refusal above is the separation-of-duties rule and nothing else.
        Assert.Null(await TryInRolledBackTx($"UPDATE {table} SET {otherUserSql} WHERE {where}"));
    }

    private async Task<long> Decisions(string eventType) => await _w.Count($"SELECT count(*) FROM audit_events WHERE event_type='{eventType}'");

    [Fact]
    public async Task Resolution_proposer_cannot_approve_own_proposal()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        var id = await _w.ExceptionId(run, "matching", "VALUE_MISMATCH");
        Assert.Equal(200, (await _w.Propose("approver1", id)).Code);

        var before = await _w.Snapshot(("exceptions", $"id='{id}'"), ("resolution_proposals", $"exception_id='{id}'"), ("approvals", $"run_id='{run}'"));
        var refused = await _w.DecideResolution("approver1", id, rid: Flow.Rid());
        refused.Is(403);
        Assert.Equal("separation-of-duties", refused.ProblemType);
        Assert.Equal(before, await _w.Snapshot(("exceptions", $"id='{id}'"), ("resolution_proposals", $"exception_id='{id}'"), ("approvals", $"run_id='{run}'")));
        Assert.Equal(0, await Decisions("RESOLUTION_DECIDED"));
        Assert.Equal("PENDING_APPROVAL", await _w.StatusOf(id));

        await AssertDbRefusesSelfApproval("approvals", "approver_user_id = proposer_user_id, status='APPROVED', decision='APPROVE'",
            $"object_type='EXCEPTION_RESOLUTION' AND run_id='{run}'", $"approver_user_id = '{World.UserId("approver2")}', status='APPROVED', decision='APPROVE'");

        (await _w.DecideResolution("approver2", id)).Is(200);
        Assert.Equal(1, await Decisions("RESOLUTION_DECIDED"));
    }

    [Fact]
    public async Task Adjustment_proposer_cannot_approve_own_adjustment()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        var adj = (await _w.ProposeAdjustment("approver1", run)).Is(201).Str("id");

        var before = await _w.Snapshot(("safeguarding_adjustments", $"id='{adj}'"), ("approvals", $"object_id='{adj}'"));
        var refused = await _w.DecideAdjustment("approver1", adj);
        refused.Is(403);
        Assert.Equal("separation-of-duties", refused.ProblemType);
        Assert.Equal(before, await _w.Snapshot(("safeguarding_adjustments", $"id='{adj}'"), ("approvals", $"object_id='{adj}'")));
        Assert.Equal(0, await Decisions("ADJUSTMENT_DECIDED"));

        await AssertDbRefusesSelfApproval("approvals", "approver_user_id = proposer_user_id, status='APPROVED', decision='APPROVE'",
            $"object_id='{adj}'", $"approver_user_id = '{World.UserId("approver2")}', status='APPROVED', decision='APPROVE'");

        (await _w.DecideAdjustment("approver2", adj)).Is(200);
    }

    [Fact]
    public async Task Manual_match_proposer_cannot_approve_own_match()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        var group = (await _w.ProposeMatch("approver1", run)).Is(201).Str("id");

        var tables = new[] { ("match_groups", $"id='{group}'"), ("match_candidates", $"match_group_id='{group}'"), ("approvals", $"object_id='{group}'"), ("exceptions", $"run_id='{run}'") };
        var before = await _w.Snapshot(tables);
        var refused = await _w.DecideMatch("approver1", run, group);
        refused.Is(403);
        Assert.Equal("separation-of-duties", refused.ProblemType);
        Assert.Equal(before, await _w.Snapshot(tables));
        Assert.Equal(0, await Decisions("MANUAL_MATCH_DECIDED"));

        await AssertDbRefusesSelfApproval("approvals", "approver_user_id = proposer_user_id, status='APPROVED', decision='APPROVE'",
            $"object_id='{group}'", $"approver_user_id = '{World.UserId("approver2")}', status='APPROVED', decision='APPROVE'");

        (await _w.DecideMatch("approver2", run, group)).Is(200);
    }

    [Fact]
    public async Task Sign_off_preparer_cannot_sign_off_own_submission()
    {
        await _w.Users();
        var run = await _w.ProcessedRun(Flow.Le);
        await _w.ClearBlockers(run);
        Assert.Equal(422, (await _w.Submit("approver1", run, requiredApprover: World.UserId("approver1"))).Code); // the preparer cannot be named as the approver either
        (await _w.Submit("approver1", run)).Is(200);

        var before = await _w.Snapshot(("runs", $"id='{run}'"), ("run_signoffs", $"run_id='{run}'"), ("approvals", $"object_type='RUN_SIGN_OFF'"));
        var refused = await _w.SignOff("approver1", run, rid: Flow.Rid());
        refused.Is(403);
        Assert.Equal("separation-of-duties", refused.ProblemType);
        Assert.Equal(before, await _w.Snapshot(("runs", $"id='{run}'"), ("run_signoffs", $"run_id='{run}'"), ("approvals", $"object_type='RUN_SIGN_OFF'")));
        Assert.Equal(0, await Decisions("RUN_SIGNED_OFF"));
        var ret = await _w.Return("approver1", run);
        Assert.Equal("separation-of-duties", ret.ProblemType); // returning is a decision too

        await AssertDbRefusesSelfApproval("run_signoffs", "signed_by_user_id = prepared_by_user_id", $"run_id='{run}'", $"signed_by_user_id = '{World.UserId("approver2")}'");
        await AssertDbRefusesSelfApproval("approvals", "approver_user_id = proposer_user_id, status='APPROVED', decision='APPROVE'",
            "object_type='RUN_SIGN_OFF'", $"approver_user_id = '{World.UserId("approver2")}', status='APPROVED', decision='APPROVE'");

        (await _w.SignOff("approver2", run)).Is(200);
    }

    [Fact]
    public async Task Configuration_proposer_cannot_activate_own_change()
    {
        await _w.Users();
        await _w.SeedRule();
        var change = (await _w.ProposeConfig("cfg1", Flow.Today)).Is(201).Str("id");

        var tables = new[] { ("config_changes", $"id='{change}'"), ("config_rules", "true"), ("ruleset_versions", "true"), ("approvals", $"object_id='{change}'") };
        var before = await _w.Snapshot(tables);
        var refused = await _w.DecideConfig("cfg1", change, rid: Flow.Rid());
        refused.Is(403);
        Assert.Equal("separation-of-duties", refused.ProblemType);
        Assert.Equal(before, await _w.Snapshot(tables));
        Assert.Equal(1, await _w.Count("SELECT count(*) FROM ruleset_versions")); // still only the initial version
        Assert.Equal(0, await Decisions("CONFIG_CHANGE_DECIDED"));

        await AssertDbRefusesSelfApproval("config_changes", "decided_by_user_id = proposed_by_user_id", $"id='{change}'", $"decided_by_user_id = '{World.UserId("cfg2")}'");
        await AssertDbRefusesSelfApproval("approvals", "approver_user_id = proposer_user_id, status='APPROVED', decision='APPROVE'",
            $"object_id='{change}'", $"approver_user_id = '{World.UserId("cfg2")}', status='APPROVED', decision='APPROVE'");

        var ok = await _w.DecideConfig("cfg2", change);
        ok.Is(200);
        Assert.Equal("ACTIVE", ok.Str("status"));
    }
}
