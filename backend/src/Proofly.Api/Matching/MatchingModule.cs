using System.Globalization;
using Npgsql;
using Proofly.Api.Matching.Commands;
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

internal sealed record MatchGroupCursor(string Id);

internal sealed class MatchingEndpoints(IMatchingRepository repo) : IEndpointModule
{
    private static readonly HashSet<string> Statuses = ["MATCHED", "UNMATCHED", "VALUE_MISMATCH", "NOT_APPLICABLE", "PENDING_APPROVAL"];

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId}/matching/workbench", GetWorkbench);
        app.MapGet("/api/v1/runs/{runId}/match-groups", ListGroups);
        app.MapGet("/api/v1/runs/{runId}/matching/unmatched", ListUnmatched);
    }

    internal static MatchGroupDto Map(MatchGroupRow g, HttpContext ctx, RunScope scope)
    {
        var caller = ctx.GetCaller();
        bool Can(Permission p) => caller.Can(p, scope.LegalEntityId);
        var locked = ManualMatchActions.RunLocked(scope.State);
        var dto = Map(g);
        var canDecide = ManualMatchActions.CanDecide(g.Status, g.Approval, caller.UserId, locked, Can);
        return dto with
        {
            Etag = g.Version > 0 ? ETags.ForVersion(g.Version) : null,
            AllowedActions = ManualMatchActions.Compute(g.Status, g.Approval, caller.UserId, locked, Can),
            Approval = dto.Approval is null ? null : dto.Approval with { CanDecide = canDecide },
        };
    }

    private async Task<RunScope> Authorize(string runId, HttpContext ctx, CancellationToken ct)
    {
        var scope = await repo.GetRunScopeAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        ctx.GetCaller().Require(Permission.MatchingView, scope.LegalEntityId);
        return scope;
    }

    private async Task<IResult> ListGroups(string runId, HttpContext ctx, CancellationToken ct)
    {
        var scope = await Authorize(runId, ctx, ct);
        string? status = ctx.Request.Query.TryGetValue("status", out var s) && s.Count > 0 ? s[0] : null;
        if (status is not null && !Statuses.Contains(status)) { throw ProblemException.Validation("status", "Unknown match status."); }
        var page = PageRequest.From(ctx);
        var cursor = page.Decode<MatchGroupCursor>();
        var fetched = await repo.ListGroupsAsync(runId, status, page.Limit, cursor?.Id, ct);
        var total = page.IncludeTotal ? (int)await repo.CountGroupsAsync(runId, status, ct) : (int?)null;
        var result = Paging.Create(fetched, page, g => new MatchGroupCursor(g.Id), total);
        return Results.Ok(new PageDto<MatchGroupDto>(result.Items.Select(g => Map(g, ctx, scope)).ToList(), result.Page));
    }

    private static readonly HashSet<string> Pairs = ["BANK_TRANSFER", "DIRECT_DEBIT", "CARD", "BANK_EVIDENCE", "LEDGER_ENTRY"];

    private async Task<IResult> ListUnmatched(string runId, HttpContext ctx, CancellationToken ct)
    {
        await Authorize(runId, ctx, ct);
        var q = ctx.Request.Query;
        string? One(string k) => q.TryGetValue(k, out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;
        var pair = One("pair");
        if (pair is not null && !Pairs.Contains(pair)) { throw ProblemException.Validation("pair", "pair must be a transaction kind (BANK_TRANSFER, DIRECT_DEBIT, CARD, BANK_EVIDENCE, LEDGER_ENTRY)."); }
        var currency = One("currency");
        if (currency is not null && (currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))) { throw ProblemException.Validation("currency", "currency must be an ISO 4217 code."); }
        var filter = new UnmatchedFilter(pair, currency, One("search"));
        var page = PageRequest.From(ctx);
        var cur = page.Decode<UnmatchedCursorState>();
        var fetched = await repo.ListUnmatchedAsync(runId, page.Limit + 1, ct, filter, cur);
        var total = page.IncludeTotal ? (int)await repo.CountUnmatchedAsync(runId, filter, ct) : (int?)null;
        var result = Paging.Create(fetched, page, c => new UnmatchedCursorState(c.GroupId, c.CandidateId), total);
        return Results.Ok(new PageDto<MatchCandidateDto>(result.Items.Select(Map).ToList(), result.Page));
    }

    private async Task<IResult> GetWorkbench(string runId, HttpContext ctx, CancellationToken ct)
    {
        var scope = await Authorize(runId, ctx, ct);
        var q = ctx.Request.Query;
        var tx = q.TryGetValue("transactionId", out var t) && t.Count > 0 && t[0] is { Length: > 0 } ts ? ts : null;
        var ex = q.TryGetValue("exceptionId", out var e) && e.Count > 0 && e[0] is { Length: > 0 } es ? es : null;
        if (tx is not null && ex is not null) { throw ProblemException.Validation("transactionId", "Provide transactionId or exceptionId, not both."); }

        var (groupId, focus, failure) = await repo.ResolveFocusAsync(runId, tx, ex, ct);
        if (failure == FocusFailure.InvalidTransactionId) { throw ProblemException.Validation("transactionId", "The transaction id is not valid."); }
        if (failure == FocusFailure.NotFound) { throw new ProblemException(ProblemTypes.NotFound); }
        var groups = new List<MatchGroupDto>();
        if (groupId is not null)
        {
            groups.Add(Map(await repo.GetGroupAsync(runId, groupId, ct) ?? throw new ProblemException(ProblemTypes.NotFound), ctx, scope));
        }
        var limit = MatchingRepository.DefaultInlineUnmatched;
        if (q.TryGetValue("unmatchedLimit", out var ul) && ul.Count > 0 && (!int.TryParse(ul[0], out limit) || limit is < 1 or > MatchingRepository.UnmatchedLimit))
        {
            throw ProblemException.Validation("unmatchedLimit", $"unmatchedLimit must be an integer from 1 to {MatchingRepository.UnmatchedLimit}.");
        }
        var unmatched = await repo.ListUnmatchedAsync(runId, limit, ct);
        var stats = await repo.GetStatsAsync(runId, ct);
        return Results.Ok(new MatchingWorkbenchDto(focus, groups, unmatched.Select(Map).ToList(), Map(stats)));
    }

    internal static MatchingStatsDto Map(MatchingStats s) => new(
        s.AutoMatchRate?.ToString("0.0###", CultureInfo.InvariantCulture), (int)s.OpenGroups, (int)s.UnmatchedCount,
        s.UnmatchedByCurrency.Select(c => new UnmatchedCurrencyDto(c.Currency, (int)c.Count, new MoneyDto(c.Value.ToString(CultureInfo.InvariantCulture), c.Currency))).ToList());

    internal static MoneyDto Map(MatchMoney m) => new(m.Amount.ToString(CultureInfo.InvariantCulture), m.Currency);

    internal static MatchCandidateDto Map(MatchCandidateRow c) => new(c.TransactionId, c.Label, Map(c.Amount), c.Timestamp, c.Reference, c.Kind, c.Side,
        c.Score?.ToString("0.0###", CultureInfo.InvariantCulture), c.Selected);

    internal static MatchGroupDto Map(MatchGroupRow g) => new(g.Id, g.Status, g.Cardinality, g.Left.Select(Map).ToList(), g.Right.Select(Map).ToList(),
        g.Difference is null ? null : Map(g.Difference), g.RuleVersion, g.Tolerance, g.Rationale, g.ExceptionIds, g.Manual, g.Approval is null ? null : Map(g.Approval));

    internal static MatchApprovalDto Map(MatchApprovalRow a) => new(a.Id, a.ObjectType, a.ObjectId, a.RunId, a.ProposalVersion,
        new UserRefDto(a.ProposerId, a.ProposerName), a.ApproverId is null ? null : new UserRefDto(a.ApproverId, a.ApproverName ?? a.ApproverId),
        a.RequiredRole, a.Status, a.Decision, a.Rationale, a.EvidenceRefs, a.PolicyVersion, a.ProposedAt, a.DecidedAt);
}
