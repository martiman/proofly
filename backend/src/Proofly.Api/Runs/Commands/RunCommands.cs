using Npgsql;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Runs;
using Proofly.Data.Audit;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Data.Runs;
using Proofly.Domain.Enums;

namespace Proofly.Api.Runs.Commands;

public static class RunCommandsModule
{
    public static IServiceCollection AddRunCommands(this IServiceCollection services)
    {
        services.AddRunsData();
        if (!services.Any(d => d.ServiceType == typeof(IJobQueue))) { services.AddJobs(); }
        services.AddRunOrchestrationData();
        services.AddSingleton<IEndpointModule, RunCommandEndpoints>();
        return services;
    }
}

/// <summary>requestRunReprocess (POST /runs/{runId}/reprocess) and compareRuns (GET /runs/{runId}/compare).</summary>
internal sealed class RunCommandEndpoints(
    IRunReadRepository reads, RunReprocessService reprocess, RunComparisonRepository comparison, NpgsqlDataSource db) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        // x-required-permission: run.reprocess; x-audit-event: RUN_REPROCESS_REQUESTED
        g.MapPost("/runs/{runId}/reprocess", async (HttpContext ctx, string runId, RunReprocessRequestDto body, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var run = await reads.GetRunAsync(RunsMapping.ScopeFor(caller, Permission.RunView), runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.RunReprocess, run.LegalEntityId);
            ctx.RequireIfMatch(ETags.ForVersion(run.RowVersion));
            var reason = body.Reason?.Trim();
            if (reason is null || reason.Length < 3) { throw ProblemException.Validation("/reason", "reason must be at least 3 characters.", "min-length"); }

            await using var cmd = db.CreateCommand("SELECT name FROM users WHERE id = @id");
            cmd.Parameters.AddWithValue("id", caller.UserId);
            var name = await cmd.ExecuteScalarAsync(ct) as string ?? caller.UserId;

            var result = await reprocess.RequestAsync(new ReprocessRequest(runId, run.RowVersion, reason, new AuditActor(ActorType.User, caller.UserId, name), ctx.GetRequestId()), ct);
            switch (result.Status)
            {
                case ReprocessStatus.NotFound: throw new ProblemException(ProblemTypes.NotFound);
                case ReprocessStatus.VersionMismatch: throw new ProblemException(ProblemTypes.PreconditionFailed, "The run changed since it was read; re-read it and retry.");
                case ReprocessStatus.Superseded: throw new ProblemException(ProblemTypes.RunSuperseded, result.Detail);
                case ReprocessStatus.InvalidState: throw new ProblemException(ProblemTypes.InvalidState, result.Detail);
            }
            var created = await reads.GetRunAsync(EntityScope.Everything, result.NewRunId!, ct) ?? throw new ProblemException(ProblemTypes.Internal);
            ctx.Set(ETags.ForVersion(created.RowVersion));
            return Results.Json(RunsMapping.Run(created), statusCode: StatusCodes.Status202Accepted);
        });

        // x-required-permission: run.view
        g.MapGet("/runs/{runId}/compare", async (HttpContext ctx, string runId, string? against, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            if (string.IsNullOrWhiteSpace(against)) { throw ProblemException.Validation("against", "against is required.", "required"); }
            var scope = RunsMapping.ScopeFor(caller, Permission.RunView);
            var run = await reads.GetRunAsync(scope, runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.RunView, run.LegalEntityId);
            var other = await reads.GetRunAsync(scope, against, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.RunView, other.LegalEntityId);
            if (other.LegalEntityId != run.LegalEntityId) { throw ProblemException.Validation("against", "The compared run must belong to the same legal entity.", "different-entity"); }

            var rows = await comparison.CompareAsync(run.Id, other.Id, ct);
            return Results.Ok(new RunComparisonDto(RunsMapping.Run(run), RunsMapping.Run(other),
                rows.Areas.Select(a => new RunComparisonAreaDto(a.Area, a.State, a.AgainstState, a.RecordCountDelta)).ToList(),
                rows.NewExceptionIds, rows.ResolvedExceptionIds, rows.ChangedExceptionIds,
                rows.SafeguardingDelta.Select(d => new ChangeDriverDto(d.Label, d.Change.Currency, RunsMapping.Money(d.Change))).ToList(),
                rows.ChangedDeliveries));
        });
    }
}
