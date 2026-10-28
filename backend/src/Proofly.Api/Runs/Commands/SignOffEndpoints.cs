using Proofly.Api.Exceptions;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Exceptions;
using Proofly.Contracts.Runs;
using Proofly.Data.Runs;
using Proofly.Data.Runs.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Runs.Commands;

public static class RunSignOffModule
{
    public static IServiceCollection AddRunSignOff(this IServiceCollection services)
    {
        services.AddSignOffData();
        services.AddSingleton<IEndpointModule, SignOffEndpoints>();
        return services;
    }
}

/// <summary>getRunSignOff, submitRunForSignOff, returnRun and signOffRun. The ETag is composed of runs.version and run_signoffs.version.</summary>
internal sealed class SignOffEndpoints(ISignOffRepository repo) : IEndpointModule
{
    private static string Etag(SignOffPackRows p) => ETags.Strong($"so-{p.Run.RowVersion}-{p.SignOff?.Version ?? 0}");

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        // x-required-permission: signoff.view
        g.MapGet("/runs/{runId}/sign-off", async (HttpContext ctx, string runId, CancellationToken ct) =>
        {
            var limit = SignOffRepository.OpenMattersDefaultLimit;
            if (ctx.Request.Query.TryGetValue("openMattersLimit", out var ol) && ol.Count > 0 && ol[0] is { Length: > 0 } ov
                && (!int.TryParse(ov, out limit) || limit is < 1 or > 200))
            {
                throw ProblemException.Validation("openMattersLimit", "openMattersLimit must be an integer from 1 to 200.");
            }
            var (caller, pack) = await LoadAsync(ctx, runId, null, ct, limit);
            ctx.Set(Etag(pack));
            return Results.Ok(ToDto(pack, caller));
        });

        // x-required-permission: signoff.view
        g.MapGet("/runs/{runId}/sign-off/open-matters", async (HttpContext ctx, string runId, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            bool? blocking = null;
            if (q.TryGetValue("blocking", out var b) && b.Count > 0 && b[0] is { Length: > 0 } bv)
            {
                blocking = bool.TryParse(bv, out var parsed) ? parsed : throw ProblemException.Validation("blocking", "blocking must be true or false.");
            }
            string? severity = null;
            if (q.TryGetValue("severity", out var sv) && sv.Count > 0 && sv[0] is { Length: > 0 } sev)
            {
                severity = sev is "CRITICAL" or "HIGH" or "MEDIUM" or "LOW" ? sev : throw ProblemException.Validation("severity", "severity must be CRITICAL, HIGH, MEDIUM or LOW.");
            }
            var page = PageRequest.From(ctx);
            var caller = ctx.GetCaller();
            var rows = await repo.ListOpenMattersAsync(RunsMapping.ScopeFor(caller, Permission.SignoffView), runId, new OpenMatterFilter(blocking, severity), page.Limit,
                page.Decode<OpenMatterCursor>(), page.IncludeTotal, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.SignoffView, rows.LegalEntityId);
            var paged = Paging.Create(rows.Items, page, i => i.Key, rows.Total);
            return Results.Ok(new PageDto<ExceptionSummaryDto>(paged.Items.Select(i => ExceptionMapping.ToSummary(i.Row)).ToList(), paged.Page));
        });

        // x-required-permission: signoff.prepare; x-audit-event: SIGN_OFF_SUBMITTED
        g.MapPost("/runs/{runId}/sign-off/submit", async (HttpContext ctx, string runId, SubmitRunRequestDto body, CancellationToken ct) =>
        {
            var (caller, pack) = await LoadAsync(ctx, runId, Permission.SignoffPrepare, ct);
            ctx.RequireIfMatch(Etag(pack));
            if (body.Note is { Length: > 2000 }) { throw ProblemException.Validation("/note", "note must be at most 2000 characters.", "max-length"); }
            if (body.RequiredApproverId is { } a && string.IsNullOrWhiteSpace(a)) { throw ProblemException.Validation("/requiredApproverId", "requiredApproverId must not be blank when given.", "required"); }
            var result = await repo.SubmitAsync(runId, body.RequiredApproverId, body.Note?.Trim(), Cx(ctx, caller, pack), ct);
            return await RespondAsync(ctx, caller, runId, result, ct);
        });

        // x-required-permission: signoff.decide; x-audit-event: SIGN_OFF_RETURNED
        g.MapPost("/runs/{runId}/sign-off/return", async (HttpContext ctx, string runId, RunReprocessRequestDto body, CancellationToken ct) =>
        {
            var (caller, pack) = await LoadAsync(ctx, runId, Permission.SignoffDecide, ct);
            ctx.RequireIfMatch(Etag(pack));
            var reason = Reason(body.Reason);
            var result = await repo.ReturnAsync(runId, reason, Cx(ctx, caller, pack), ct);
            return await RespondAsync(ctx, caller, runId, result, ct);
        });

        // x-required-permission: signoff.decide; x-audit-event: RUN_SIGNED_OFF
        g.MapPost("/runs/{runId}/sign-off/approve", async (HttpContext ctx, string runId, SignOffRunRequestDto body, CancellationToken ct) =>
        {
            var (caller, pack) = await LoadAsync(ctx, runId, Permission.SignoffDecide, ct);
            ctx.RequireIfMatch(Etag(pack));
            if (body.Attestation != true) { throw ProblemException.Validation("/attestation", "The attestation must be accepted (true) to sign off.", "required"); }
            var reason = Reason(body.Reason);
            var result = await repo.ApproveAsync(runId, reason, body.AttestationTextHash, Cx(ctx, caller, pack), ct);
            return await RespondAsync(ctx, caller, runId, result, ct);
        });
    }

    private static string Reason(string? reason)
    {
        var r = reason?.Trim();
        return r is null || r.Length < 3 ? throw ProblemException.Validation("/reason", "reason must be at least 3 characters.", "min-length") : r;
    }

    private static SignOffCommandContext Cx(HttpContext ctx, Caller caller, SignOffPackRows pack) =>
        new(caller.UserId, ctx.GetRequestId(), (int)pack.Run.RowVersion, pack.SignOff?.Version ?? 0);

    /// <summary>404 (missing or outside the caller's scope) then 403 (no permission in that entity).</summary>
    private async Task<(Caller, SignOffPackRows)> LoadAsync(HttpContext ctx, string runId, Permission? command, CancellationToken ct, int openMattersLimit = SignOffRepository.OpenMattersDefaultLimit)
    {
        var caller = ctx.GetCaller();
        var pack = await repo.GetPackAsync(RunsMapping.ScopeFor(caller, Permission.SignoffView), runId, openMattersLimit, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.SignoffView, pack.Run.LegalEntityId);
        if (command is { } p) { caller.Require(p, pack.Run.LegalEntityId); }
        return (caller, pack);
    }

    private async Task<IResult> RespondAsync(HttpContext ctx, Caller caller, string runId, SignOffResult result, CancellationToken ct)
    {
        switch (result.Status)
        {
            case SignOffStatus.NotFound: throw new ProblemException(ProblemTypes.NotFound);
            case SignOffStatus.VersionMismatch: throw new ProblemException(ProblemTypes.PreconditionFailed, result.Detail);
            case SignOffStatus.InvalidState or SignOffStatus.Blocked: throw new ProblemException(ProblemTypes.InvalidState, result.Detail);
            case SignOffStatus.RunSuperseded: throw new ProblemException(ProblemTypes.RunSuperseded, result.Detail);
            case SignOffStatus.StaleSignOff: throw new ProblemException(ProblemTypes.StaleSignoff, result.Detail);
            case SignOffStatus.SeparationOfDuties: throw new ProblemException(ProblemTypes.SeparationOfDuties, result.Detail);
            case SignOffStatus.Forbidden: throw new ProblemException(ProblemTypes.Forbidden, result.Detail);
            case SignOffStatus.Invalid: throw ProblemException.Validation(result.Pointer ?? "", result.Detail ?? "Invalid.");
        }
        var pack = await repo.GetPackAsync(EntityScope.Everything, runId, ct) ?? throw new ProblemException(ProblemTypes.Internal);
        ctx.Set(Etag(pack));
        return Results.Ok(ToDto(pack, caller));
    }

    private static readonly string[] SubmitStates = ["READY_FOR_REVIEW", "RETURNED", "PREPARATION_INCOMPLETE"];

    internal static IReadOnlyList<string> AllowedActions(SignOffPackRows p, Caller caller)
    {
        var le = p.Run.LegalEntityId;
        var actions = new List<string>();
        if (p.Run.State == "SUPERSEDED") { return actions; }
        if (SubmitStates.Contains(p.Run.SignOffState) && p.Blockers.Count == 0 && caller.Can(Permission.SignoffPrepare, le)) { actions.Add("SUBMIT"); }
        var pending = p.Run.SignOffState == "APPROVAL_PENDING" && p.SignOff?.State == "APPROVAL_PENDING";
        var mayDecide = pending && caller.Can(Permission.SignoffDecide, le) && p.SignOff!.PreparedByUserId != caller.UserId
            && (p.SignOff.RequiredApproverUserId is null || p.SignOff.RequiredApproverUserId == caller.UserId);
        if (mayDecide)
        {
            actions.Add("RETURN");
            if (p.Blockers.Count == 0 && !p.StaleSinceSubmission) { actions.Add("APPROVE"); }
        }
        return actions;
    }

    internal static RunSignOffDto ToDto(SignOffPackRows p, Caller caller)
    {
        var allowed = AllowedActions(p, caller);
        var a = p.Approval;
        var approval = a is null ? null : new ApprovalDto(a.Id, a.ObjectType, a.ObjectId, a.RunId, a.ProposalVersion, new UserRefDto(a.Proposer.Id, a.Proposer.Name),
            a.Approver is null ? null : new UserRefDto(a.Approver.Id, a.Approver.Name), a.RequiredRole, a.Status, a.Decision, a.Rationale, [], a.PolicyVersion, a.ProposedAt, a.DecidedAt,
            a.Status == "PENDING" && a.Proposer.Id != caller.UserId && caller.Can(Permission.SignoffDecide, p.Run.LegalEntityId));
        return new RunSignOffDto(
            RunsMapping.Run(p.Run),
            p.Checklist.Select(c => new ChecklistItemDto(c.Control, c.Control, Label(c.Control), c.State, true,
                $"{c.FindingCount} findings, {c.BlockingFindingCount} blocking", c.ExceptionIds, c.OpenCount)).ToList(),
            p.OpenMatters.Select(ExceptionMapping.ToSummary).ToList(), p.OpenMattersTotal, p.OpenMattersByCategory.Select(x => new OpenMatterCategoryDto(x.Control, x.Total, x.Blocking)).ToList(), p.Qualifications, p.ChangesSincePreparation, p.AttestationText, p.AttestationTextHash,
            allowed.Contains("APPROVE"), p.Blockers, p.EvidencePackId, approval,
            p.History.Select(h => new TimelineEventDto("h-" + h.Id, h.At, new ActorDto(h.ActorType, h.ActorId, h.ActorName), h.Type == "RUN_SIGNED_OFF" ? "APPROVAL" : "STATUS_CHANGE", HistoryText(h))).ToList(),
            allowed);
    }

    private static string HistoryText(SignOffHistoryRow h) =>
        (h.Type switch { "SIGN_OFF_SUBMITTED" => "Submitted for sign-off", "SIGN_OFF_RETURNED" => "Returned to the preparer", "SIGN_OFF_INVALIDATED" => "Sign-off attempt refused as stale", _ => "Signed off" }) + (h.Reason is null ? "" : ": " + h.Reason);

    private static string Label(string control) => control switch
    {
        "data_completeness" => "Data completeness", "matching" => "Matching", "independent_ledger" => "Independent ledger",
        "transaction_lifecycle" => "Transaction lifecycle", "safeguarding" => "Safeguarding", _ => control,
    };
}
