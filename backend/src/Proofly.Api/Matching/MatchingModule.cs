using System.Globalization;
using Npgsql;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Matching;
using Proofly.Data.Matching;
using Proofly.Domain.Enums;

namespace Proofly.Api.Matching;

public static class MatchingModule
{
    public static IServiceCollection AddMatching(this IServiceCollection services)
    {
        services.AddSingleton<IMatchingRepository>(sp => new MatchingRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IEndpointModule, MatchingEndpoints>();
        return services;
    }
}

/// <summary>Uses the platform PageInfo so a null total is omitted (the contract types total as integer).</summary>
internal sealed record MatchGroupPageResponse(IReadOnlyList<MatchGroupDto> Items, PageInfo Page);

internal sealed record MatchGroupCursor(string Id);

internal sealed class MatchingEndpoints(IMatchingRepository repo) : IEndpointModule
{
    private static readonly HashSet<string> Statuses = ["MATCHED", "UNMATCHED", "VALUE_MISMATCH", "NOT_APPLICABLE", "PENDING_APPROVAL"];

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId}/matching/workbench", GetWorkbench);
        app.MapGet("/api/v1/runs/{runId}/match-groups", ListGroups);
    }

    private async Task<RunScope> Authorize(string runId, HttpContext ctx, CancellationToken ct)
    {
        var scope = await repo.GetRunScopeAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        ctx.GetCaller().Require(Permission.MatchingView, scope.LegalEntityId);
        return scope;
    }

    private async Task<IResult> ListGroups(string runId, HttpContext ctx, CancellationToken ct)
    {
        await Authorize(runId, ctx, ct);
        string? status = ctx.Request.Query.TryGetValue("status", out var s) && s.Count > 0 ? s[0] : null;
        if (status is not null && !Statuses.Contains(status)) { throw ProblemException.Validation("status", "Unknown match status."); }
        var page = PageRequest.From(ctx);
        var cursor = page.Decode<MatchGroupCursor>();
        var fetched = await repo.ListGroupsAsync(runId, status, page.Limit, cursor?.Id, ct);
        var total = page.IncludeTotal ? (int)await repo.CountGroupsAsync(runId, status, ct) : (int?)null;
        var result = Paging.Create(fetched, page, g => new MatchGroupCursor(g.Id), total);
        return Results.Ok(new MatchGroupPageResponse(result.Items.Select(Map).ToList(), result.Page));
    }

    private async Task<IResult> GetWorkbench(string runId, HttpContext ctx, CancellationToken ct)
    {
        await Authorize(runId, ctx, ct);
        var q = ctx.Request.Query;
        var tx = q.TryGetValue("transactionId", out var t) && t.Count > 0 && t[0] is { Length: > 0 } ts ? ts : null;
        var ex = q.TryGetValue("exceptionId", out var e) && e.Count > 0 && e[0] is { Length: > 0 } es ? es : null;
        if (tx is not null && ex is not null) { throw ProblemException.Validation("transactionId", "Provide transactionId or exceptionId, not both."); }

        var (groupId, focus, failure) = await repo.ResolveFocusAsync(runId, tx, ex, ct);
        if (failure == FocusFailure.InvalidTransactionId) { throw ProblemException.Validation("transactionId", "The transaction id is not valid."); }
        if (groupId is null || focus is null) { throw new ProblemException(ProblemTypes.NotFound); }
        var group = await repo.GetGroupAsync(runId, groupId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        var unmatched = await repo.ListUnmatchedAsync(runId, MatchingRepository.UnmatchedLimit, ct);
        return Results.Ok(new MatchingWorkbenchDto(focus, [Map(group)], unmatched.Select(Map).ToList()));
    }

    internal static MoneyDto Map(MatchMoney m) => new(m.Amount.ToString(CultureInfo.InvariantCulture), m.Currency);

    internal static MatchCandidateDto Map(MatchCandidateRow c) => new(c.TransactionId, c.Label, Map(c.Amount), c.Timestamp, c.Reference);

    internal static MatchGroupDto Map(MatchGroupRow g)
    {
        var rationale = $"{g.Status} {g.Cardinality} group of {g.Left.Count} left and {g.Right.Count} right record(s) under {g.RuleVersion}"
            + (g.Difference is { } d ? $"; difference {d.Amount.ToString(CultureInfo.InvariantCulture)} {d.Currency}" : "")
            + $" (tolerance {g.Tolerance}).";
        return new MatchGroupDto(g.Id, g.Status, g.Left.Select(Map).ToList(), g.Right.Select(Map).ToList(),
            g.Difference is null ? null : Map(g.Difference), g.RuleVersion, g.Tolerance, rationale, g.ExceptionIds, g.Manual, g.Approval is null ? null : Map(g.Approval));
    }

    internal static MatchApprovalDto Map(MatchApprovalRow a) => new(a.Id, a.ObjectType, a.ObjectId, a.RunId, a.ProposalVersion,
        new UserRefDto(a.ProposerId, a.ProposerName), a.ApproverId is null ? null : new UserRefDto(a.ApproverId, a.ApproverName ?? a.ApproverId),
        a.RequiredRole, a.Status, a.Decision, a.Rationale, a.EvidenceRefs, a.PolicyVersion, a.ProposedAt, a.DecidedAt);
}
