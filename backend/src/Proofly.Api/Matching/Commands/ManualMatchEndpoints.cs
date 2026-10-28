using Npgsql;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Matching;
using Proofly.Data.Audit;
using Proofly.Data.Matching;
using Proofly.Data.Matching.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Matching.Commands;

public static class ManualMatchModule
{
    public static readonly ProblemType RecordAlreadyUsed = new("record-already-used", 409, "Record already used by another match");

    public static IServiceCollection AddMatchingCommands(this IServiceCollection services)
    {
        services.AddSingleton<IManualMatchCommands>(sp => new ManualMatchCommands(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IAuditWriter>()));
        services.AddSingleton<IEndpointModule, ManualMatchEndpoints>();
        return services;
    }
}

internal sealed class ManualMatchEndpoints(IMatchingRepository repo, IManualMatchCommands commands, NpgsqlDataSource db) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/runs/{runId}/match-groups", Propose);
        app.MapPost("/api/v1/runs/{runId}/match-groups/{groupId}/decision", Decide);
    }

    private async Task<string> UserName(string userId, CancellationToken ct)
    {
        await using var c = db.CreateCommand("SELECT name FROM users WHERE id = @u");
        c.Parameters.AddWithValue("u", userId);
        return await c.ExecuteScalarAsync(ct) as string ?? userId;
    }

    private static ProblemException ToProblem(ManualMatchResult r) => r.Outcome switch
    {
        ManualMatchOutcome.RunNotFound or ManualMatchOutcome.GroupNotFound => new ProblemException(ProblemTypes.NotFound),
        ManualMatchOutcome.Validation => ProblemException.Validation(r.Pointer ?? "/", r.Detail ?? "Invalid input."),
        ManualMatchOutcome.RecordAlreadyUsed => new ProblemException(ManualMatchModule.RecordAlreadyUsed, r.Detail),
        ManualMatchOutcome.InvalidState => new ProblemException(ProblemTypes.InvalidState, r.Detail),
        ManualMatchOutcome.StaleProposal => new ProblemException(ProblemTypes.StaleProposal, r.Detail),
        ManualMatchOutcome.RunSuperseded => new ProblemException(ProblemTypes.RunSuperseded, r.Detail),
        ManualMatchOutcome.RunLocked => new ProblemException(ProblemTypes.InvalidState, r.Detail),
        ManualMatchOutcome.StaleVersion => new ProblemException(ProblemTypes.PreconditionFailed, r.Detail),
        ManualMatchOutcome.SeparationOfDuties => new ProblemException(ProblemTypes.SeparationOfDuties, r.Detail),
        _ => new ProblemException(ProblemTypes.Internal, r.Detail),
    };

    private async Task<IResult> Propose(string runId, ProposeManualMatchRequest? body, HttpContext ctx, CancellationToken ct)
    {
        var scope = await repo.GetRunScopeAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        var caller = ctx.GetCaller();
        caller.Require(Permission.MatchingPropose, scope.LegalEntityId);
        if (body is null) { throw ProblemException.Validation("/", "A request body is required."); }
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length < 3) { throw ProblemException.Validation("/reason", "A reason of at least 3 characters is required."); }
        if (body.LeftTransactionIds is not { Count: > 0 }) { throw ProblemException.Validation("/leftTransactionIds", "Select at least one left record."); }
        if (body.RightTransactionIds is not { Count: > 0 }) { throw ProblemException.Validation("/rightTransactionIds", "Select at least one right record."); }

        var res = await commands.ProposeAsync(new ProposeManualMatchCommand(runId, caller.UserId, await UserName(caller.UserId, ct), body.LeftTransactionIds,
            body.RightTransactionIds, reason, body.EvidenceRefs ?? [], ctx.GetRequestId()), ct);
        if (res.Outcome != ManualMatchOutcome.Ok) { throw ToProblem(res); }

        var group = await repo.GetGroupAsync(runId, res.GroupId!, ct) ?? throw new ProblemException(ProblemTypes.Internal, "The proposed group could not be read back.");
        var dto = MatchingEndpoints.Map(group, ctx, scope);
        ctx.Set(ETags.ForVersion(group.Version));
        ctx.Response.Headers.Location = $"/api/v1/runs/{runId}/match-groups?status=PENDING_APPROVAL";
        return Results.Json(dto, statusCode: StatusCodes.Status201Created, options: Platform.Json.WireJson.Options);
    }

    private async Task<IResult> Decide(string runId, string groupId, DecideManualMatchRequest? body, HttpContext ctx, CancellationToken ct)
    {
        var scope = await repo.GetRunScopeAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        var caller = ctx.GetCaller();
        caller.Require(Permission.ExceptionApprove, scope.LegalEntityId);
        var group = await repo.GetGroupAsync(runId, groupId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        if (body is null) { throw ProblemException.Validation("/", "A request body is required."); }
        var errors = new List<FieldError>();
        if (body.Decision is not ("APPROVE" or "REJECT")) { errors.Add(new FieldError("/decision", "Decision must be APPROVE or REJECT.")); }
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length < 3) { errors.Add(new FieldError("/reason", "A reason of at least 3 characters is required.")); }
        if (errors.Count > 0) { throw new ProblemException(ProblemTypes.Validation, "Invalid decision.", errors); }

        if (group.Approval is { Status: "PENDING" } pending && pending.ProposerId == caller.UserId)
        {
            throw new ProblemException(ProblemTypes.SeparationOfDuties, "The proposer cannot decide their own manual match.");
        }
        ctx.RequireIfMatch(ETags.ForVersion(group.Version));

        var res = await commands.DecideAsync(new DecideManualMatchCommand(runId, groupId, caller.UserId, await UserName(caller.UserId, ct), body.Decision == "APPROVE",
            reason, body.ProposalVersion, group.Version, ctx.GetRequestId()), ct);
        if (res.Outcome != ManualMatchOutcome.Ok) { throw ToProblem(res); }

        var approval = await commands.GetApprovalAsync(res.ApprovalId!, ct) ?? throw new ProblemException(ProblemTypes.Internal, "The approval could not be read back.");
        if (body.Decision == "APPROVE" && await repo.GetGroupAsync(runId, groupId, ct) is { } updated) { ctx.Set(ETags.ForVersion(updated.Version)); }
        return Results.Json(MatchingEndpoints.Map(approval) with { CanDecide = false }, options: Platform.Json.WireJson.Options);
    }
}
