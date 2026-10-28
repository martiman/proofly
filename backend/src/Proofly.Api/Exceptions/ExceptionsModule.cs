using System.Text.Json;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Exceptions;
using Proofly.Data.Exceptions;
using Proofly.Domain.Enums;

namespace Proofly.Api.Exceptions;

public static class ExceptionsModule
{
    public static IServiceCollection AddExceptions(this IServiceCollection services)
    {
        services.AddSingleton<IExceptionReadRepository, ExceptionReadRepository>();
        services.AddSingleton<IEndpointModule, ExceptionsEndpoints>();
        return services;
    }
}

/// <summary>Cursor keyset state: the normalized sort and the sort-key values of the last returned row.</summary>
public sealed record ExceptionCursor(string S, string[] V);

internal sealed class ExceptionsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/runs/{runId}/exceptions", ListAsync);
        app.MapGet("/api/v1/runs/{runId}/exceptions/summary", SummaryAsync);
        app.MapGet("/api/v1/exceptions/{exceptionId}", DetailAsync);
    }

    /// <summary>Legal entities in which the caller holds exception.view; null = all entities.</summary>
    internal static IReadOnlyCollection<string>? Scope(Caller c)
    {
        var all = c.LegalEntityIds.Contains(Caller.AllEntities);
        var wire = WireNames.ToPermission(Permission.ExceptionView);
        if (c.PermissionsByLegalEntity.Count == 0) { return all ? null : c.LegalEntityIds.ToArray(); }
        var star = c.PermissionsByLegalEntity.TryGetValue(Caller.AllEntities, out var s) && s.Contains(wire);
        if (star) { return all ? null : c.LegalEntityIds.ToArray(); }
        return c.PermissionsByLegalEntity.Where(kv => kv.Key != Caller.AllEntities && kv.Value.Contains(wire) && (all || c.LegalEntityIds.Contains(kv.Key)))
            .Select(kv => kv.Key).ToArray();
    }

    private static async Task<(ExceptionFilter Filter, Caller Caller, IReadOnlyCollection<string>? Scope)> PrepareAsync(
        string runId, HttpContext ctx, IExceptionReadRepository repo, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var le = await repo.FindRunLegalEntityAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.ExceptionView, le);

        JsonElement? view = null;
        if (ctx.Request.Query.TryGetValue("savedViewId", out var id) && id.Count > 0 && !string.IsNullOrEmpty(id[0]))
        {
            var json = await repo.FindSavedViewFiltersAsync(id[0]!, caller.UserId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            view = JsonDocument.Parse(json).RootElement.Clone();
        }
        return (ExceptionQueryParser.Parse(ctx.Request.Query, view), caller, Scope(caller));
    }

    private static async Task<IResult> ListAsync(string runId, HttpContext ctx, IExceptionReadRepository repo, TimeProvider clock, CancellationToken ct)
    {
        var (filter, _, scope) = await PrepareAsync(runId, ctx, repo, ct);
        var page = PageRequest.From(ctx);
        var sortRaw = ctx.Request.Query.TryGetValue("sort", out var sv) && sv.Count > 0 ? sv[0] : null;
        if (!ExceptionSortSpec.TryParse(sortRaw, out var sort, out var error)) { throw ProblemException.Validation("sort", error!); }

        string[]? after = null;
        if (page.Decode<ExceptionCursor>() is { } cur)
        {
            if (cur.S != sort.Normalized || cur.V is null || cur.V.Length != sort.Keys.Count)
            {
                throw ProblemException.Validation("cursor", "The cursor does not belong to this sort.");
            }
            after = cur.V;
        }

        var result = await repo.ListAsync(new ExceptionListQuery(runId, scope, filter, sort, page.Limit, after, page.IncludeTotal, clock.GetUtcNow()), ct);
        var paged = Paging.Create(result.Rows, page, r => new ExceptionCursor(sort.Normalized, r.SortKeys.ToArray()), result.Total);
        return Results.Ok(new PageDto<ExceptionSummaryDto>(paged.Items.Select(ExceptionMapping.ToSummary).ToList(), paged.Page));
    }

    private static async Task<IResult> SummaryAsync(string runId, HttpContext ctx, IExceptionReadRepository repo, TimeProvider clock, CancellationToken ct)
    {
        var (filter, _, scope) = await PrepareAsync(runId, ctx, repo, ct);
        var data = await repo.SummaryAsync(runId, scope, filter, clock.GetUtcNow(), ct);
        return Results.Ok(ExceptionMapping.ToDto(data));
    }

    private static async Task<IResult> DetailAsync(string exceptionId, HttpContext ctx, IExceptionReadRepository repo, TimeProvider clock, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var data = await repo.GetDetailAsync(exceptionId, Scope(caller), clock.GetUtcNow(), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.ExceptionView, data.Row.LegalEntityId);
        var dto = ExceptionMapping.ToDetail(data, caller);
        ctx.Set(dto.Etag);
        return Results.Ok(dto);
    }
}
