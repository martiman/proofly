using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Exceptions;
using Proofly.Data.Exceptions;
using Proofly.Data.Exceptions.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Exceptions.Commands;

public static class ResolutionCommandsModule
{
    public static IServiceCollection AddResolutionCommands(this IServiceCollection services)
    {
        services.AddResolutionCommandsData();
        services.AddSingleton<IEndpointModule, ResolutionCommandEndpoints>();
        return services;
    }
}

internal sealed class ResolutionCommandEndpoints : IEndpointModule
{
    private static readonly string[] Kinds = ["RESOLVED", "ACCEPTED_DIFFERENCE", "FALSE_POSITIVE"];

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");
        g.MapPost("/exceptions/{exceptionId}/resolution-proposals", ProposeAsync);
        g.MapPost("/exceptions/{exceptionId}/resolution-proposals/{proposalId}/decision", DecideAsync);
        g.MapPost("/exceptions/{exceptionId}/reopen", ReopenAsync);
    }

    private static ProblemException ToProblem(CommandOutcome o) => o.Status switch
    {
        CommandStatus.NotFound => new ProblemException(ProblemTypes.NotFound),
        CommandStatus.Stale => new ProblemException(ProblemTypes.PreconditionFailed, o.Detail),
        CommandStatus.InvalidState => new ProblemException(ProblemTypes.InvalidState, o.Detail),
        CommandStatus.StaleProposal => new ProblemException(ProblemTypes.StaleProposal, o.Detail),
        CommandStatus.RunSuperseded => new ProblemException(ProblemTypes.RunSuperseded, o.Detail),
        CommandStatus.Forbidden when o.Pointer == "separation-of-duties" => new ProblemException(ProblemTypes.SeparationOfDuties, o.Detail),
        CommandStatus.Forbidden => new ProblemException(ProblemTypes.Forbidden, o.Detail),
        _ => ProblemException.Validation(o.Pointer ?? "", o.Detail ?? "Invalid."),
    };

    private static string Reason(string? value)
    {
        if (value is null || value.Trim().Length < 3) { throw ProblemException.Validation("/reason", "A reason of at least 3 characters is required."); }
        return value.Trim();
    }

    /// <summary>404 (missing or out of scope) -> 403 (no permission here) -> 428/412 (If-Match), in that order.</summary>
    private static async Task<ExceptionRef> AuthoriseAsync(string id, HttpContext ctx, IExceptionCommandRepository cmds, Permission permission)
    {
        var caller = ctx.GetCaller();
        var r = await cmds.FindAsync(id, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(permission, r.LegalEntityId);
        ctx.RequireIfMatch(ETags.ForVersion(r.Version));
        return r;
    }

    private static async Task<IResult> DetailAsync(string id, HttpContext ctx, IExceptionReadRepository read, TimeProvider clock)
    {
        var caller = ctx.GetCaller();
        var data = await read.GetDetailAsync(id, ExceptionsEndpoints.Scope(caller), clock.GetUtcNow(), ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        var dto = ExceptionMapping.ToDetail(data, caller);
        ctx.Set(dto.Etag);
        return Results.Ok(dto);
    }

    private static CommandContext Cx(HttpContext ctx) => new(ctx.GetCaller().UserId, ctx.GetRequestId());

    private static async Task<IResult> ProposeAsync(string exceptionId, ResolutionProposalRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IResolutionCommandRepository res, IExceptionReadRepository read, TimeProvider clock)
    {
        var r = await AuthoriseAsync(exceptionId, ctx, cmds, Permission.ExceptionPropose);
        if (body.Kind is null || !Kinds.Contains(body.Kind)) { throw ProblemException.Validation("/kind", "kind must be RESOLVED, ACCEPTED_DIFFERENCE or FALSE_POSITIVE."); }
        var reason = Reason(body.Reason);
        if (body.EvidenceRefs is not { Count: > 0 }) { throw ProblemException.Validation("/evidenceRefs", "At least one evidence reference is required."); }
        if (body.EvidenceRefs.Any(string.IsNullOrWhiteSpace)) { throw ProblemException.Validation("/evidenceRefs", "Evidence references must not be empty."); }
        var outcome = await res.ProposeAsync(exceptionId, r.Version, body.Kind, reason, body.EvidenceRefs.Select(x => x.Trim()).ToList(), Cx(ctx), ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return await DetailAsync(exceptionId, ctx, read, clock);
    }

    private static async Task<IResult> DecideAsync(string exceptionId, string proposalId, ResolutionDecisionRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IResolutionCommandRepository res, IExceptionReadRepository read, TimeProvider clock)
    {
        var r = await AuthoriseAsync(exceptionId, ctx, cmds, Permission.ExceptionApprove);
        if (body.Decision is not ("APPROVE" or "REJECT")) { throw ProblemException.Validation("/decision", "decision must be APPROVE or REJECT."); }
        var reason = Reason(body.Reason);
        var outcome = await res.DecideAsync(exceptionId, proposalId, r.Version, body.Decision == "APPROVE", reason, body.ProposalVersion, Cx(ctx), ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return await DetailAsync(exceptionId, ctx, read, clock);
    }

    private static async Task<IResult> ReopenAsync(string exceptionId, ReopenExceptionRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IResolutionCommandRepository res, IExceptionReadRepository read, TimeProvider clock)
    {
        var r = await AuthoriseAsync(exceptionId, ctx, cmds, Permission.ExceptionReopen);
        var outcome = await res.ReopenAsync(exceptionId, r.Version, Reason(body.Reason), Cx(ctx), ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return await DetailAsync(exceptionId, ctx, read, clock);
    }
}
