using System.Text.Json;
using Proofly.Domain.Enums;
using Proofly.Domain.Ids;

namespace Proofly.Acceptance.Tests.Support;

/// <summary>The user-facing steps of the daily control cycle, every one through the real API. Preconditions (ETags) are read fresh, as a client would.</summary>
public static class Flow
{
    public const string Le = "LE-ACC-A";
    public const string LeB = "LE-ACC-B";
    public static readonly string RunV1 = $"RUN-{Le}-20261027-v1";
    public static readonly string RunV2 = $"RUN-{Le}-20261027-v2";

    public static readonly string[] Refs = ["EV-statement-2026-10-27"];

    /// <summary>One analyst (and a second), two approvers, a viewer, two configuration administrators and a security administrator, all scoped to every entity.</summary>
    public static async Task Users(this World w)
    {
        await w.AddUser("analyst1", (Role.Analyst, "*"));
        await w.AddUser("analyst2", (Role.Analyst, "*"));
        await w.AddUser("approver1", (Role.Approver, "*"));
        await w.AddUser("approver2", (Role.Approver, "*"));
        await w.AddUser("viewer1", (Role.Viewer, "*"));
        await w.AddUser("cfg1", (Role.ConfigAdmin, "*"));
        await w.AddUser("cfg2", (Role.ConfigAdmin, "*"));
        await w.AddUser("sec1", (Role.SecurityAdmin, "*"));
    }

    public static string Rid() => Guid.NewGuid().ToString();

    public static async Task<string> ExceptionId(this World w, string run, string control, string reasonCode) =>
        await w.Scalar<string>($"SELECT id FROM exceptions WHERE run_id='{run}' AND control='{control}' AND reason_code='{reasonCode}' ORDER BY id LIMIT 1")
        ?? throw new InvalidOperationException($"no {control}/{reasonCode} exception in {run}");

    public static Task<List<string>> BlockingIds(this World w, string run) =>
        w.Strings($"SELECT id FROM exceptions WHERE run_id='{run}' AND blocking ORDER BY id");

    public static async Task<string> StatusOf(this World w, string exceptionId) => (await w.Scalar<string>($"SELECT status FROM exceptions WHERE id='{exceptionId}'"))!;

    // ---------------------------------------------------------------- exceptions

    public static async Task<Resp> Assign(this World w, string user, string id, string owner, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/exceptions/{id}/assign", new { ownerId = owner, note = "Taking this one" },
            await w.ETag(user, $"/api/v1/exceptions/{id}"), key, rid);

    public static async Task<Resp> Note(this World w, string user, string id, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/exceptions/{id}/notes", new { text = "Checked the bank statement" },
            await w.ETag(user, $"/api/v1/exceptions/{id}"), key, rid);

    public static async Task<Resp> Propose(this World w, string user, string id, string kind = "ACCEPTED_DIFFERENCE", string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/exceptions/{id}/resolution-proposals",
            new { kind, reason = "Timing difference confirmed against the bank statement", evidenceRefs = Refs },
            await w.ETag(user, $"/api/v1/exceptions/{id}"), key, rid);

    public static async Task<string> PendingProposalId(this World w, string exceptionId) =>
        (await w.Scalar<string>($"SELECT id FROM resolution_proposals WHERE exception_id='{exceptionId}' AND status='PENDING'"))!;

    public static async Task<Resp> DecideResolution(this World w, string user, string id, bool approve = true, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/exceptions/{id}/resolution-proposals/{await w.PendingProposalId(id)}/decision",
            new { decision = approve ? "APPROVE" : "REJECT", reason = "Independent review of the evidence done" },
            await w.ETag(user, $"/api/v1/exceptions/{id}"), key, rid);

    public static async Task<Resp> Reopen(this World w, string user, string id, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/exceptions/{id}/reopen", new { reason = "New information arrived" },
            await w.ETag(user, $"/api/v1/exceptions/{id}"), key, rid);

    /// <summary>Propose by <paramref name="proposer"/> and approve by <paramref name="approver"/> (different users).</summary>
    public static async Task Resolve(this World w, string id, string proposer = "analyst1", string approver = "approver1", string kind = "ACCEPTED_DIFFERENCE")
    {
        var p = await w.Propose(proposer, id, kind);
        p.Is(200);
        var d = await w.DecideResolution(approver, id);
        d.Is(200);
    }

    // ---------------------------------------------------------------- adjustments

    public static async Task<Resp> ProposeAdjustment(this World w, string user, string run, string? rid = null, string? key = null, string amount = "100.00") =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/safeguarding/adjustments",
            new { currency = "GBP", amount = new { amount, currency = "GBP" }, reason = "Pending client money transfer in transit", evidenceRefs = Refs }, null, key, rid);

    public static async Task<string> VersionTag(this World w, string table, string id) =>
        $"\"v{await w.Scalar<int>($"SELECT version FROM {table} WHERE id='{id}'")}\"";

    public static async Task<Resp> DecideAdjustment(this World w, string user, string adjustmentId, bool approve = true, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/safeguarding/adjustments/{adjustmentId}/decision",
            new { decision = approve ? "APPROVE" : "REJECT", reason = "Evidence of the transfer in transit seen" },
            await w.VersionTag("safeguarding_adjustments", adjustmentId), key, rid);

    // ---------------------------------------------------------------- manual matches

    /// <summary>The JPY transfer and its bank transaction (5 000 against 5 200: a value mismatch) as contract transaction ids.</summary>
    public static async Task<(string Left, string Right)> JpyPair(this World w, string run)
    {
        var t = await w.Scalar<long>($"SELECT t.id FROM transfer_instructions t JOIN source_deliveries d ON d.id=t.delivery_id WHERE d.run_id='{run}' AND t.currency='JPY'");
        var b = await w.Scalar<long>($"SELECT t.id FROM bank_transactions t JOIN source_deliveries d ON d.id=t.delivery_id WHERE d.run_id='{run}' AND t.currency='JPY'");
        return (TransactionIds.Format("transfer_instructions", t), TransactionIds.Format("bank_transactions", b));
    }

    public static async Task<Resp> ProposeMatch(this World w, string user, string run, string? rid = null, string? key = null)
    {
        var (l, r) = await w.JpyPair(run);
        return await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/match-groups",
            new { leftTransactionIds = new[] { l }, rightTransactionIds = new[] { r }, reason = "Bank booked a fee on top; confirmed on the statement", evidenceRefs = Refs }, null, key, rid);
    }

    public static async Task<Resp> DecideMatch(this World w, string user, string run, string groupId, bool approve = true, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/match-groups/{groupId}/decision",
            new { decision = approve ? "APPROVE" : "REJECT", reason = "Fee confirmed independently" },
            await w.VersionTag("match_groups", groupId), key, rid);

    // ---------------------------------------------------------------- sign-off

    public static async Task<Resp> Submit(this World w, string user, string run, string? requiredApprover = null, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/submit", new { requiredApproverId = requiredApprover, note = "All blocking matters closed" },
            await w.ETag(user, $"/api/v1/runs/{run}/sign-off"), key, rid);

    public static async Task<Resp> Return(this World w, string user, string run, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/return", new { reason = "Please add the missing statement" },
            await w.ETag(user, $"/api/v1/runs/{run}/sign-off"), key, rid);

    public static async Task<Resp> SignOff(this World w, string user, string run, string? rid = null, string? key = null, bool attestation = true)
    {
        var pack = await w.Get(user, $"/api/v1/runs/{run}/sign-off");
        return await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/sign-off/approve",
            new { attestation, attestationTextHash = pack.Body.GetProperty("attestationTextHash").GetString(), reason = "Reviewed the evidence pack and the resolutions" },
            pack.ETag, key, rid);
    }

    // ---------------------------------------------------------------- evidence packs

    public static async Task<string> EvidencePack(this World w, string user, string run, string? rid = null)
    {
        var res = await w.Send(user, HttpMethod.Post, $"/api/v1/runs/{run}/evidence-pack", null, null, null, rid);
        res.Is(202);
        await w.Drain();
        return res.Str("id");
    }

    // ---------------------------------------------------------------- whole stages

    /// <summary>Resolves everything that blocks sign-off, each with a proposal by an analyst and an approval by a different approver.</summary>
    public static async Task ClearBlockers(this World w, string run)
    {
        foreach (var id in await w.Strings($"SELECT id FROM exceptions WHERE run_id='{run}' AND blocking AND status NOT IN ('RESOLVED','ACCEPTED_DIFFERENCE','FALSE_POSITIVE') ORDER BY id")) { await w.Resolve(id); }
    }

    public static async Task<Resp> RunDto(this World w, string user, string run)
    {
        var r = await w.Get(user, $"/api/v1/runs/{run}");
        r.Is(200);
        return r;
    }

    /// <summary>v1 processed, blockers cleared, an evidence pack made, prepared by analyst1 and signed off by approver2.</summary>
    public static async Task<string> SignedOffRun(this World w)
    {
        await w.Users();
        var run = await w.ProcessedRun(Le);
        await w.ClearBlockers(run);
        await w.EvidencePack("approver1", run);
        (await w.Submit("analyst1", run)).Is(200);
        (await w.SignOff("approver2", run)).Is(200);
        return run;
    }

    // ---------------------------------------------------------------- configuration changes

    public const string RuleId = "RULE-MATCH-TOL";

    public static Task SeedRule(this World w) => w.Exec("""
        INSERT INTO config_rules (id, kind, name, config_version, payload) VALUES ('RULE-MATCH-TOL', 'rule', 'Matching tolerance', 'v1',
          '{"control":"matching","description":"Amount tolerance for matching","tolerance":"0.01 GBP","severity":"MEDIUM","effectiveFrom":"2026-01-01"}')
        """);

    public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    public static string Tomorrow => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

    public static async Task<Resp> ProposeConfig(this World w, string user, string effectiveFrom, string tolerance = "0.05 GBP", string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, "/api/v1/settings/changes", new
        {
            targetKind = "RULE", targetId = RuleId, effectiveFrom, reason = "Tolerance aligned with the bank fee schedule",
            after = new { control = "matching", description = "Amount tolerance for matching", tolerance, severity = "MEDIUM", effectiveFrom },
        }, null, key, rid);

    public static async Task<Resp> DecideConfig(this World w, string user, string changeId, bool approve = true, string? rid = null, string? key = null) =>
        await w.Send(user, HttpMethod.Post, $"/api/v1/settings/changes/{changeId}/decision",
            new { decision = approve ? "APPROVE" : "REJECT", reason = "Fee schedule checked by a second administrator" },
            await w.ETag(user, $"/api/v1/settings/changes/{changeId}"), key, rid);

    public static JsonElement[] Items(this Resp r) => (r.Body.ValueKind == JsonValueKind.Array ? r.Body : r.Body.GetProperty("items")).EnumerateArray().ToArray();
}
