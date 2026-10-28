using System.Globalization;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Safeguarding;
using Proofly.Data.Safeguarding;
using Proofly.Domain.Enums;

namespace Proofly.Api.Safeguarding;

public static class SafeguardingModule
{
    public static IServiceCollection AddSafeguarding(this IServiceCollection services)
    {
        services.AddSafeguardingData();
        services.AddSingleton<IEndpointModule, SafeguardingEndpoints>();
        return services;
    }
}

/// <summary>getSafeguardingPosition and listAdjustments (read side). Writes (propose/decide) belong to other issues.</summary>
internal sealed class SafeguardingEndpoints(ISafeguardingReadRepository repo) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/runs/{runId}/safeguarding");
        g.MapGet("", GetPosition);
        g.MapGet("/adjustments", ListAdjustments);
        g.MapGet("/{currency}/lines", ListLines);
    }

    private static EntityScope ScopeOf(Caller c) => c.LegalEntityIds.Contains(Caller.AllEntities) ? EntityScope.Everything : new EntityScope(false, c.LegalEntityIds.ToArray());

    private async Task<IResult> GetPosition(string runId, HttpContext ctx, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var linesLimit = SafeguardingReadRepository.DefaultLinesLimit;
        if (ctx.Request.Query.TryGetValue("linesLimit", out var ll) && ll.Count > 0 && ll[0] is { Length: > 0 } lv
            && (!int.TryParse(lv, out linesLimit) || linesLimit is < 1 or > 200))
        {
            throw ProblemException.Validation("linesLimit", "linesLimit must be an integer from 1 to 200.");
        }
        var view = await repo.GetPositionAsync(runId, ScopeOf(caller), linesLimit, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.SafeguardingView, view.Run.LegalEntityId);
        return Results.Ok(Map(view, caller));
    }

    private async Task<IResult> ListLines(string runId, string currency, HttpContext ctx, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var q = ctx.Request.Query;
        var kind = q.TryGetValue("kind", out var k) && k.Count > 0 ? k[0] : null;
        if (kind is not ("obligation" or "balance")) { throw ProblemException.Validation("kind", "kind is required and must be obligation or balance.", "required"); }
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) { throw ProblemException.Validation("currency", "currency must be an ISO 4217 code."); }
        var search = q.TryGetValue("search", out var sv) && sv.Count > 0 && sv[0] is { Length: > 0 } str ? str : null;
        if (search is { Length: > 200 }) { throw ProblemException.Validation("search", "search must be at most 200 characters.", "max-length"); }
        var run = await repo.GetRunAsync(runId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.SafeguardingView, run.LegalEntityId);
        var page = PageRequest.From(ctx);
        var rows = await repo.ListLinesAsync(runId, currency, kind, search, page.Limit, page.Decode<LineKey>(), page.IncludeTotal, ScopeOf(caller), ct)
            ?? throw new ProblemException(ProblemTypes.NotFound);
        PageInfoDto info;
        if (kind == "obligation") { info = Paging.Create(rows.Obligations, page, i => i.Key, rows.Total).Page; }
        else { info = Paging.Create(rows.Balances, page, i => i.Key, rows.Total).Page; }
        var obligations = rows.Obligations.Take(page.Limit).Select(i => new ObligationLineDto(i.Line.AccountId, i.Line.Currency, M(i.Line.Amount, i.Line.Currency), i.Line.MovementType)).ToList();
        var balances = rows.Balances.Take(page.Limit).Select(i => ToDto(i.Line)).ToList();
        return Results.Ok(new SafeguardingLinePageDto(kind, currency, obligations, balances, info));
    }

    private static EligibleBalanceLineDto ToDto(BalanceLineRow b) => new(b.SourceRowId, b.RecordId, b.ExternalAccountId, b.BalanceTimestamp, M(b.Balance, b.Currency),
        b.EvidenceReference, b.Included, b.Reason, b.FindingIds);

    private async Task<IResult> ListAdjustments(string runId, HttpContext ctx, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var run = await repo.GetRunAsync(runId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.SafeguardingView, run.LegalEntityId);
        var status = ctx.Request.Query.TryGetValue("status", out var s) && s.Count > 0 && s[0] is { Length: > 0 } v ? v : null;
        var page = PageRequest.From(ctx);
        var result = await repo.ListAdjustmentsPageAsync(runId, status, page.Limit, page.Decode<AdjustmentCursor>(), page.IncludeTotal, ScopeOf(caller), ct)
            ?? throw new ProblemException(ProblemTypes.NotFound);
        var paged = Paging.Create(result.HasMore ? result.Items.Append(result.Items[^1]).ToList() : result.Items, page, a => new AdjustmentCursor(a.CreatedAt, a.Id), result.Total);
        return Results.Ok(new PageDto<SafeguardingAdjustmentDto>(paged.Items.Select(r => Map(r, caller, run.LegalEntityId)).ToList(), paged.Page));
    }

    private static string D(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    private static MoneyDto M(decimal v, string ccy) => new(D(v), ccy);

    internal static SafeguardingPositionDto Map(SafeguardingView v, Caller caller)
    {
        var prev = v.Previous.ToDictionary(p => p.Currency);
        var positions = v.Positions.Select(p => new SafeguardingCurrencyPositionDto(
            p.Currency, M(p.Obligation, p.Currency), M(p.Eligible, p.Currency), M(p.ApprovedAdjustments, p.Currency), p.Result is { } res ? M(res, p.Currency) : null,
            p.State, p.Qualifications, prev.TryGetValue(p.Currency, out var pp) && pp.Result is { } pr ? M(pr, p.Currency) : null, p.ExceptionIds, p.Stale,
            v.ObligationLinesTotal.GetValueOrDefault(p.Currency), v.BalanceLinesTotal.GetValueOrDefault(p.Currency))).ToList();

        var drivers = new List<ChangeDriverDto>();
        foreach (var p in v.Positions.Where(p => prev.ContainsKey(p.Currency)))
        {
            var o = prev[p.Currency];
            void Add(string label, decimal now, decimal before)
            {
                if (now != before) { drivers.Add(new ChangeDriverDto(label, p.Currency, M(now - before, p.Currency))); }
            }
            Add("Client obligation", p.Obligation, o.Obligation);
            Add("Eligible balances", p.Eligible, o.Eligible);
            Add("Approved adjustments", p.ApprovedAdjustments, o.ApprovedAdjustments);
        }

        return new SafeguardingPositionDto(v.Run.RunId, v.Run.BusinessDate, v.Run.LegalEntityId, v.EvidenceReadiness, v.Run.SignOffState, positions,
            v.Obligations.Select(l => new ObligationLineDto(l.AccountId, l.Currency, M(l.Amount, l.Currency), l.MovementType)).ToList(),
            v.Balances.Select(ToDto).ToList(),
            v.Adjustments.Select(a => Map(a, caller, v.Run.LegalEntityId)).ToList(), drivers, v.Positions.Any(p => p.Stale));
    }

    internal static SafeguardingAdjustmentDto Map(AdjustmentRow a, Caller caller, string legalEntityId) => new(
        a.Id, a.RunId, a.Currency, M(a.Amount, a.Currency), a.Status, new UserRefDto(a.Owner.Id, a.Owner.Name), a.Reason,
        a.EvidenceRefs, a.ExpiresOn,
        a.Approval is null ? null : new ApprovalDto(a.Approval.Id, a.Approval.ObjectType, a.Approval.ObjectId, a.Approval.RunId, a.Approval.ProposalVersion,
            new UserRefDto(a.Approval.Proposer.Id, a.Approval.Proposer.Name),
            a.Approval.Approver is null ? null : new UserRefDto(a.Approval.Approver.Id, a.Approval.Approver.Name),
            a.Approval.RequiredRole, a.Approval.Status, a.Approval.Decision, a.Approval.Rationale, a.Approval.EvidenceRefs, a.Approval.PolicyVersion,
            a.Approval.ProposedAt, a.Approval.DecidedAt,
            a.Approval.Status == "PENDING" && a.Approval.Proposer.Id != caller.UserId && caller.Can(Permission.SafeguardingAdjustmentApprove, legalEntityId)),
        a.History.Select(h => new TimelineEventDto(h.Id, h.At, new ActorDto(h.ActorType, h.ActorId, h.ActorName),
            h.EventType.Contains("APPROV", StringComparison.Ordinal) ? "APPROVAL" : "STATUS_CHANGE", h.Text)).ToList());
}
