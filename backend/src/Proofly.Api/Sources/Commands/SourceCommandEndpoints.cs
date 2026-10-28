using System.Text.Json;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Json;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Sources;
using Proofly.Data.Audit;
using Proofly.Data.Sources;
using Proofly.Data.Sources.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Sources.Commands;

public static class SourceCommandsModule
{
    public static IServiceCollection AddSourceCommands(this IServiceCollection services)
    {
        Proofly.Data.Sources.Commands.SourceCommandsDataModule.AddSourceCommands(services);
        services.AddSingleton<IEndpointModule, SourceCommandEndpoints>();
        return services;
    }
}

/// <summary>retryDeliveryIngestion and markDeliverySuperseded. Permissions per x-required-permission are enforced by the platform; scope and per-entity permission here.</summary>
internal sealed class SourceCommandEndpoints(ISourceReadRepository reads, ISourceCommandRepository commands) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/deliveries/{deliveryId}/retry-ingestion", async (string deliveryId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var body = await ReadBodyAsync<ReasonRequestDto>(ctx, ct);
            var reason = ValidReason(body?.Reason);
            var d = await LoadAsync(caller, deliveryId, Permission.SourceRetry, ct);
            ctx.RequireIfMatch(ETags.ForVersion(d.Version));
            var result = await commands.RetryIngestionAsync(deliveryId, d.Version, Actor(caller), reason, ctx.GetRequestId(), ct);
            return await RespondAsync(ctx, caller, deliveryId, result, StatusCodes.Status202Accepted, ct);
        });

        app.MapPost("/api/v1/deliveries/{deliveryId}/mark-superseded", async (string deliveryId, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var body = await ReadBodyAsync<MarkSupersededRequestDto>(ctx, ct);
            var reason = ValidReason(body?.Reason);
            if (string.IsNullOrWhiteSpace(body?.SupersededByDeliveryId)) { throw ProblemException.Validation("/supersededByDeliveryId", "The correcting delivery id is required."); }
            var d = await LoadAsync(caller, deliveryId, Permission.SourceSupersede, ct);
            ctx.RequireIfMatch(ETags.ForVersion(d.Version));
            var result = await commands.MarkSupersededAsync(deliveryId, body.SupersededByDeliveryId.Trim(), d.Version, Actor(caller), reason, ctx.GetRequestId(), ct);
            return await RespondAsync(ctx, caller, deliveryId, result, StatusCodes.Status200OK, ct);
        });
    }

    private static AuditActor Actor(Caller c) => new(ActorType.User, c.UserId, c.UserId);

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, CancellationToken ct) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(WireJson.Options, ct); }
        catch (JsonException) { throw ProblemException.Validation("", "The request body is not valid JSON for this operation."); }
    }

    private static string ValidReason(string? reason)
    {
        var r = reason?.Trim();
        if (r is null || r.Length < 3) { throw ProblemException.Validation("/reason", "A reason of at least 3 characters is required."); }
        if (r.Length > 4000) { throw ProblemException.Validation("/reason", "The reason must not exceed 4000 characters."); }
        return r;
    }

    /// <summary>404 when the delivery is not visible (unknown or out of scope), 403 when the caller may view it but lacks the permission for its legal entity.</summary>
    private async Task<DeliverySummary> LoadAsync(Caller caller, string deliveryId, Permission permission, CancellationToken ct)
    {
        var detail = await reads.GetDeliveryAsync(deliveryId, SourceEndpoints.ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        var d = detail.Delivery;
        SourceEndpoints.Require(caller, d);
        var allowed = d.LegalEntityId is null ? caller.Has(permission) : caller.Can(permission, d.LegalEntityId);
        if (!allowed) { throw new ProblemException(ProblemTypes.Forbidden, "The caller lacks the required permission for this legal entity."); }
        return d;
    }

    private async Task<IResult> RespondAsync(HttpContext ctx, Caller caller, string deliveryId, SourceCommandResult result, int status, CancellationToken ct)
    {
        switch (result.Status)
        {
            case SourceCommandStatus.NotFound: throw new ProblemException(ProblemTypes.NotFound);
            case SourceCommandStatus.VersionMismatch: throw new ProblemException(ProblemTypes.PreconditionFailed, "The resource changed since it was read; re-read it and retry.");
            case SourceCommandStatus.InvalidState: throw new ProblemException(ProblemTypes.InvalidState, result.Detail);
            case SourceCommandStatus.Invalid: throw ProblemException.Validation(result.Pointer ?? "", result.Detail ?? "Invalid request.");
        }
        var detail = await reads.GetDeliveryAsync(deliveryId, SourceEndpoints.ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        ctx.Set(ETags.ForVersion(detail.Delivery.Version));
        return Results.Json(SourceEndpoints.ToDetailDto(detail, caller), WireJson.Options, statusCode: status);
    }
}
