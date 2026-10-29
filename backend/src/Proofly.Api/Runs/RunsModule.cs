using System.Globalization;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Runs;
using Proofly.Data.Runs;
using Proofly.Domain.Enums;

namespace Proofly.Api.Runs;

public static class RunsModule
{
    public static IServiceCollection AddRuns(this IServiceCollection services)
    {
        services.AddRunsData();
        services.AddSingleton<IEndpointModule, RunsEndpoints>();
        return services;
    }
}

internal static class RunsMapping
{
    /// <summary>Entities in which the caller holds the permission (per-entity role breakdown honoured).</summary>
    public static EntityScope ScopeFor(Caller c, Permission permission)
    {
        var wire = WireNames.ToPermission(permission);
        if (c.PermissionsByLegalEntity.Count == 0)
        {
            return c.LegalEntityIds.Contains(Caller.AllEntities) ? EntityScope.Everything : new EntityScope(false, c.LegalEntityIds.ToList());
        }
        if (c.PermissionsByLegalEntity.TryGetValue(Caller.AllEntities, out var all) && all.Contains(wire)) { return EntityScope.Everything; }
        return new EntityScope(false, c.PermissionsByLegalEntity.Where(kv => kv.Value.Contains(wire) && kv.Key != Caller.AllEntities).Select(kv => kv.Key).ToList());
    }

    public static string Amount(decimal d) => d.ToString("0.00####", CultureInfo.InvariantCulture);
    public static MoneyDto Money(MoneyRow m) => new(Amount(m.Amount), m.Currency);
    public static UserRefDto? User(UserRefRow? u) => u is null ? null : new(u.Id, u.Name);

    public static RunDto Run(RunRow r) => new(r.Id, r.LegalEntityId, r.BusinessDate, r.Version, r.RulesetVersion, r.State, r.SignOffState, r.StartedAt,
        r.LastEvidenceArrivalAt, r.SupersededByRunId, r.SupersedesRunId, User(r.Preparer), User(r.RequiredApprover), User(r.SignedOffBy), r.SignedOffAt);

    public static ControlRoomDto ControlRoom(ControlRoomRows c) => new(
        Run(c.Run),
        c.Summary.Select(s => new DailySummaryItemDto(s.Text, s.LinkKind is null ? null : new DailySummaryLinkDto(s.LinkKind, s.LinkId!))).ToList(),
        c.EvidenceReadiness.Select(d => new ControlRoomDeliveryDto(d.Id, d.SourceName, d.FileName, d.BusinessDate, d.SchemaVersion, d.DeclaredRecordCount,
            d.ActualRecordCount, d.DeclaredTotals.Select(Money).ToList(), d.ExpectedBy, d.ReceivedAt, d.CorrectionOfDeliveryId, d.SupersededByDeliveryId,
            d.State, d.AcceptedRecords, d.RejectedRecords, [], d.FindingIds, d.FindingCount)).ToList(),
        c.ControlAreas.Select(a => new ControlAreaSummaryDto(a.Area, a.Label, a.State, a.RecordCount, a.Value.Select(Money).ToList(), a.OpenExceptions,
            a.PreviousRecordCount, a.ChangeFromPrevious)).ToList(),
        c.AttentionQueue.Select(e => new ControlRoomExceptionDto(e.Id, e.RunId, e.Title, e.Description, e.Control, e.ReasonCode, e.Severity, e.Status,
            e.Value is null ? null : Money(e.Value), e.LegalEntityId, e.BusinessDate, User(e.Owner), e.CreatedAt, e.AgeDays, e.DueAt, e.Blocking,
            e.SourceFile, e.SourceRowId, ETags.ForVersion(e.RowVersion))).ToList(),
        c.Safeguarding.Select(s => new SafeguardingCurrencyPositionDto(s.Currency, Money(s.Obligation), Money(s.EligibleBalance), Money(s.ApprovedAdjustments),
            s.Result is null ? null : Money(s.Result), s.State, s.Qualifications, s.PreviousResult is null ? null : Money(s.PreviousResult), s.ExceptionIds, s.Stale, s.ExceptionCount)).ToList(),
        c.BlockingCount, c.OpenExceptionsTotal);
}

internal sealed class RunsEndpoints(IRunReadRepository repo) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        // x-required-permission: run.view
        g.MapGet("/legal-entities", async (HttpContext ctx, CancellationToken ct) =>
        {
            var rows = await repo.ListLegalEntitiesAsync(RunsMapping.ScopeFor(ctx.GetCaller(), Permission.RunView), ct);
            return Results.Ok(rows.Select(r => new LegalEntityDto(r.Id, r.Name, r.Currencies)).ToList());
        });

        g.MapGet("/runs", async (HttpContext ctx, string? legalEntityId, string? businessDate, bool? includeSuperseded, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            if (legalEntityId is not null) { caller.Require(Permission.RunView, legalEntityId); }
            DateOnly? date = null;
            if (businessDate is not null)
            {
                date = DateOnly.TryParseExact(businessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d : throw ProblemException.Validation("businessDate", "businessDate must be YYYY-MM-DD.");
            }
            var page = PageRequest.From(ctx);
            var rows = await repo.ListRunsAsync(RunsMapping.ScopeFor(caller, Permission.RunView), new RunListFilter(legalEntityId, date, includeSuperseded ?? false),
                page.Limit, page.Decode<RunCursor>(), page.IncludeTotal, ct);
            var paged = Paging.Create(rows.Items, page, r => new RunCursor(r.BusinessDate, r.LegalEntityId, r.Version, r.Id), rows.Total);
            return Results.Ok(new RunPageDto(paged.Items.Select(RunsMapping.Run).ToList(), paged.Page));
        });

        g.MapGet("/runs/{runId}", async (HttpContext ctx, string runId, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var run = await repo.GetRunAsync(RunsMapping.ScopeFor(caller, Permission.RunView), runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.RunView, run.LegalEntityId);
            var etag = ETags.ForVersion(run.RowVersion);
            ctx.Set(etag);
            return Results.Ok(RunsMapping.Run(run));
        });

        g.MapGet("/runs/{runId}/control-room", async (HttpContext ctx, string runId, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var room = await repo.GetControlRoomAsync(RunsMapping.ScopeFor(caller, Permission.RunView), runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.RunView, room.Run.LegalEntityId);
            return Results.Ok(RunsMapping.ControlRoom(room));
        });
    }
}
