using System.Text.Json;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Exceptions;
using Proofly.Data.Exceptions;
using Proofly.Data.Exceptions.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Exceptions.Commands;

public static class ExceptionCommandsModule
{
    public static IServiceCollection AddExceptionCommands(this IServiceCollection services)
    {
        services.AddExceptionCommandsData();
        services.AddSingleton<IEndpointModule, ExceptionCommandEndpoints>();
        return services;
    }
}

internal sealed class ExceptionCommandEndpoints : IEndpointModule
{
    /// <summary>Query-parameter names a saved view may carry (the listExceptions filters; paging is not part of a view).</summary>
    private static readonly HashSet<string> ViewFilterKeys = new(StringComparer.Ordinal)
    {
        "severity", "control", "status", "ownerId", "unassigned", "blocking", "legalEntityId", "currency", "valueMin", "valueMax",
        "businessDateFrom", "businessDateTo", "ageDaysMin", "ageDaysMax", "dueBefore", "search",
    };

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");
        g.MapPost("/exceptions/{exceptionId}/assign", AssignAsync);
        g.MapPost("/exceptions/{exceptionId}/notes", NoteAsync);
        g.MapPost("/exceptions/{exceptionId}/request-evidence", EvidenceAsync);
        g.MapPost("/runs/{runId}/exceptions/bulk-assign", BulkAssignAsync);
        g.MapGet("/exception-views", ListViewsAsync);
        g.MapPost("/exception-views", CreateViewAsync);
        g.MapDelete("/exception-views/{viewId}", DeleteViewAsync);
    }

    private static ProblemException ToProblem(CommandOutcome o) => o.Status switch
    {
        CommandStatus.NotFound => new ProblemException(ProblemTypes.NotFound),
        CommandStatus.Stale => new ProblemException(ProblemTypes.PreconditionFailed, o.Detail),
        CommandStatus.InvalidState => new ProblemException(ProblemTypes.InvalidState, o.Detail),
        CommandStatus.RunSuperseded => new ProblemException(ProblemTypes.RunSuperseded, o.Detail),
        CommandStatus.Forbidden => new ProblemException(ProblemTypes.Forbidden, o.Detail),
        CommandStatus.Duplicate => ProblemException.Validation(o.Pointer ?? "", o.Detail ?? "Duplicate.", "duplicate"),
        _ => ProblemException.Validation(o.Pointer ?? "", o.Detail ?? "Invalid.", o.Status == CommandStatus.LimitReached ? "limit-reached" : null),
    };

    private static string RequireText(string? value, string pointer)
    {
        if (string.IsNullOrWhiteSpace(value)) { throw ProblemException.Validation(pointer, "Must not be empty or whitespace."); }
        return value;
    }

    private static string? CheckNote(string? note)
    {
        if (note is null) { return null; }
        if (note.Trim().Length < 3) { throw ProblemException.Validation("/note", "A note must be at least 3 characters when given."); }
        return note;
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

    private static async Task<IResult> AssignAsync(string exceptionId, AssignExceptionRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IExceptionReadRepository read, TimeProvider clock)
    {
        var r = await AuthoriseAsync(exceptionId, ctx, cmds, Permission.ExceptionAssign);
        var note = CheckNote(body.Note);
        if (body.OwnerId is not null) { RequireText(body.OwnerId, "/ownerId"); }
        var outcome = await cmds.AssignAsync(exceptionId, r.Version, body.OwnerId, note, Cx(ctx), ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return await DetailAsync(exceptionId, ctx, read, clock);
    }

    private static async Task<IResult> NoteAsync(string exceptionId, ExceptionNoteRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IExceptionReadRepository read, TimeProvider clock)
    {
        var r = await AuthoriseAsync(exceptionId, ctx, cmds, Permission.ExceptionInvestigate);
        var outcome = await cmds.AddNoteAsync(exceptionId, r.Version, RequireText(body.Text, "/text"), Cx(ctx), ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return await DetailAsync(exceptionId, ctx, read, clock);
    }

    private static async Task<IResult> EvidenceAsync(string exceptionId, RequestEvidenceRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IExceptionReadRepository read, TimeProvider clock)
    {
        var r = await AuthoriseAsync(exceptionId, ctx, cmds, Permission.ExceptionInvestigate);
        var from = string.IsNullOrWhiteSpace(body.RequestedFrom) ? null : body.RequestedFrom.Trim();
        var outcome = await cmds.RequestEvidenceAsync(exceptionId, r.Version, RequireText(body.Text, "/text"), from, Cx(ctx), ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return await DetailAsync(exceptionId, ctx, read, clock);
    }

    private static async Task<IResult> BulkAssignAsync(string runId, BulkAssignRequest body, HttpContext ctx,
        IExceptionCommandRepository cmds, IExceptionReadRepository read)
    {
        var caller = ctx.GetCaller();
        var le = await read.FindRunLegalEntityAsync(runId, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.ExceptionAssign, le);

        if (body.Items is not { Count: > 0 and <= 200 }) { throw ProblemException.Validation("/items", "Between 1 and 200 items are required."); }
        RequireText(body.OwnerId, "/ownerId");
        var note = CheckNote(body.Note);
        var dup = body.Items.GroupBy(i => i.ExceptionId).FirstOrDefault(x => x.Count() > 1);
        if (dup is not null) { throw ProblemException.Validation("/items", $"Exception {dup.Key} appears more than once."); }

        var results = new List<BulkItemResultDto>(body.Items.Count);
        foreach (var item in body.Items)
        {
            ProblemException? problem = null;
            string? etag = null;
            try
            {
                var r = await cmds.FindAsync(item.ExceptionId, ctx.RequestAborted);
                if (r is null || r.RunId != runId) { throw new ProblemException(ProblemTypes.NotFound); }
                caller.Require(Permission.ExceptionAssign, r.LegalEntityId);
                var current = ETags.ForVersion(r.Version);
                if (!ETags.StrongMatch(item.Etag, current)) { throw new ProblemException(ProblemTypes.PreconditionFailed, "The exception changed since it was read; re-read it and retry."); }
                var outcome = await cmds.AssignAsync(item.ExceptionId, r.Version, body.OwnerId, note, Cx(ctx), ctx.RequestAborted);
                if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
                etag = ETags.ForVersion((await cmds.FindAsync(item.ExceptionId, ctx.RequestAborted))!.Version);
            }
            catch (ProblemException ex) { problem = ex; }

            results.Add(problem is null
                ? new BulkItemResultDto(item.ExceptionId, 200, etag, null)
                : new BulkItemResultDto(item.ExceptionId, problem.ProblemType.Status, null,
                    new BulkProblemDto(problem.ProblemType.Type, problem.ProblemType.Title, problem.ProblemType.Status, problem.Detail)));
        }
        var dto = new BulkResultDto(results);
        return results.All(x => x.Status == 200) ? Results.Ok(dto) : Results.Json(dto, statusCode: StatusCodes.Status207MultiStatus);
    }

    // ---- saved views ----

    private static SavedViewDto ToDto(SavedViewRow v) => new(v.Id, v.Name, v.Shared, JsonDocument.Parse(v.FiltersJson).RootElement.Clone(), v.Sort,
        new UserRefDto(v.OwnerId, v.OwnerName), v.CreatedAt);

    private static async Task<IResult> ListViewsAsync(HttpContext ctx, IExceptionCommandRepository cmds)
    {
        var rows = await cmds.ListViewsAsync(ctx.GetCaller().UserId, ctx.RequestAborted);
        return Results.Ok(new SavedViewListDto(rows.Select(ToDto).ToList()));
    }

    private static async Task<IResult> CreateViewAsync(SavedViewInputDto body, HttpContext ctx, IExceptionCommandRepository cmds)
    {
        var name = RequireText(body.Name, "/name").Trim();
        if (body.Shared == true) { throw ProblemException.Validation("/shared", "Shared views are not supported; saved views are personal."); }
        if (body.Filters.ValueKind != JsonValueKind.Object) { throw ProblemException.Validation("/filters", "filters must be an object."); }
        foreach (var p in body.Filters.EnumerateObject().Where(p => !ViewFilterKeys.Contains(p.Name)))
        {
            throw ProblemException.Validation("/filters/" + p.Name, $"'{p.Name}' is not a listExceptions filter.", "unknown-filter");
        }
        try { ExceptionQueryParser.Parse(new QueryCollection(), body.Filters); }
        catch (ProblemException ex) when (ex.Errors is { Count: > 0 })
        {
            throw ProblemException.Validation("/filters/" + ex.Errors[0].Pointer, ex.Errors[0].Message);
        }
        if (body.Sort is not null && !ExceptionSortSpec.TryParse(body.Sort, out _, out var sortError)) { throw ProblemException.Validation("/sort", sortError!); }

        var (outcome, view) = await cmds.CreateViewAsync(ctx.GetCaller().UserId, name, body.Filters.GetRawText(), body.Sort, ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        ctx.Set(ETags.ForVersion(view!.Version));
        return Results.Json(ToDto(view), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteViewAsync(string viewId, HttpContext ctx, IExceptionCommandRepository cmds)
    {
        var userId = ctx.GetCaller().UserId;
        var view = await cmds.FindViewAsync(viewId, userId, ctx.RequestAborted) ?? throw new ProblemException(ProblemTypes.NotFound);
        if (view.OwnerId != userId) { throw new ProblemException(ProblemTypes.Forbidden, "Only the owner can delete a saved view."); }
        ctx.RequireIfMatch(ETags.ForVersion(view.Version));
        var outcome = await cmds.DeleteViewAsync(viewId, userId, view.Version, ctx.RequestAborted);
        if (outcome.Status != CommandStatus.Ok) { throw ToProblem(outcome); }
        return Results.NoContent();
    }
}
