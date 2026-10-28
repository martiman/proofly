using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Governance;
using Proofly.Data.Governance.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Governance.Commands;

public static class GovernanceCommandsModule
{
    public static IServiceCollection AddGovernanceCommands(this IServiceCollection services)
    {
        services.AddGovernanceCommandsData();
        services.AddSingleton<IProblemExceptionMapper, GovernanceCommandProblemMapper>();
        services.AddSingleton<IEndpointModule, ConfigChangeEndpoints>();
        return services;
    }
}

internal sealed class GovernanceCommandProblemMapper : IProblemExceptionMapper
{
    public ProblemException? Map(Exception exception) => exception is not GovernanceCommandException g ? null : g.Failure switch
    {
        GovernanceFailure.NotFound => new ProblemException(ProblemTypes.NotFound, g.Message),
        GovernanceFailure.InvalidState => new ProblemException(ProblemTypes.InvalidState, g.Message),
        GovernanceFailure.Duplicate => new ProblemException(ProblemTypes.Duplicate, g.Message),
        GovernanceFailure.SeparationOfDuties => new ProblemException(ProblemTypes.SeparationOfDuties, g.Message),
        GovernanceFailure.Forbidden => new ProblemException(ProblemTypes.Forbidden, g.Message),
        _ => ProblemException.Validation(g.Pointer ?? "", g.Message),
    };
}

internal sealed class ConfigChangeEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/settings/changes");

        g.MapGet("", async (HttpContext ctx, IConfigChangeRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var q = ctx.Request.Query;
            string? S(string k) => q.TryGetValue(k, out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;
            var page = PageRequest.From(ctx);
            var rows = await repo.ListAsync(new ConfigChangeQuery(S("status"), S("targetKind"), page.Limit, page.Decode<ConfigChangeCursor>()), ct);
            var items = rows.Items.Select(r => Map(r, caller)).ToList();
            var next = rows.HasMore ? Cursors.Encode(new ConfigChangeCursor(rows.Items[^1].ProposedAt, rows.Items[^1].Id)) : null;
            return Results.Ok(new PageDto<ConfigChangeDto>(items, new PageInfoDto(next, rows.HasMore)));
        });

        g.MapGet("/{changeId}", async (string changeId, HttpContext ctx, IConfigChangeRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var row = await repo.GetAsync(changeId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            ctx.Set(ETags.ForVersion(row.Version));
            return Results.Ok(Map(row, caller));
        });

        g.MapPost("", async (HttpContext ctx, IConfigChangeRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var body = await ReadBodyAsync(ctx, ct);
            var kind = body["targetKind"]!.GetValue<string>();
            var targetId = body["targetId"]!.GetValue<string>();
            var after = body["after"] as JsonObject ?? throw ProblemException.Validation("/after", "after must be an object.");
            var effective = DateOnly.ParseExact(body["effectiveFrom"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            var (_, errors) = ConfigPayloadValidator.Validate(kind, targetId, after);
            if (errors.Count > 0) { throw new ProblemException(ProblemTypes.Validation, "The proposed object is invalid.", errors.Select(e => new FieldError(e.Pointer, e.Message)).ToList()); }
            if (kind == ConfigTargetKinds.UserAccess)
            {
                if (!caller.Has(Permission.AccessManage)) { throw new ProblemException(ProblemTypes.Forbidden, "Proposing an access change needs access.manage."); }
                foreach (var le in after["legalEntityIds"]!.AsArray().Select(n => n!.GetValue<string>())) { caller.Require(Permission.AccessManage, le); }
            }

            var row = await repo.ProposeAsync(new ProposeConfigChangeCommand(kind, targetId, after, effective, body["reason"]!.GetValue<string>(), caller.UserId, ctx.GetRequestId()), ct);
            ctx.Set(ETags.ForVersion(row.Version));
            ctx.Response.Headers.Location = $"/api/v1/settings/changes/{row.Id}";
            return Results.Json(Map(row, caller), WireJsonOptions, statusCode: StatusCodes.Status201Created);
        });

        g.MapPost("/{changeId}/decision", async (string changeId, HttpContext ctx, IConfigChangeRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var body = await ReadBodyAsync(ctx, ct);
            var approve = body["decision"]!.GetValue<string>() == "APPROVE";
            int? proposalVersion = body["proposalVersion"] is { } pv ? pv.GetValue<int>() : null;
            var result = await repo.DecideAsync(new DecideConfigChangeCommand(changeId, approve, body["reason"]!.GetValue<string>(), proposalVersion, caller.UserId,
                ctx.GetRequestId(), change => AuthorizeDecision(ctx, caller, change)), ct);
            ctx.Set(ETags.ForVersion(result.Change.Version));
            return Results.Ok(Map(result.Change, caller));
        });
    }

    private static readonly JsonSerializerOptions WireJsonOptions = Proofly.Api.Platform.Json.WireJson.Options;

    /// <summary>Scope (404/403), permission for the change kind (403), then If-Match against the locked row (428/412).</summary>
    private static void AuthorizeDecision(HttpContext ctx, Caller caller, ConfigChangeRow change)
    {
        if (change.IsAccess)
        {
            if (!caller.Has(Permission.AccessManage)) { throw new ProblemException(ProblemTypes.Forbidden, "Deciding an access change needs access.manage."); }
            foreach (var le in change.After["legalEntityIds"]!.AsArray().Select(n => n!.GetValue<string>())) { caller.Require(Permission.AccessManage, le); }
        }
        else if (!caller.Has(Permission.SettingsApprove)) { throw new ProblemException(ProblemTypes.Forbidden, "Deciding a configuration change needs settings.approve."); }
        ctx.RequireIfMatch(ETags.ForVersion(change.Version));
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Request.Body.Position = 0;
        return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct) as JsonObject ?? throw ProblemException.Validation("", "A JSON object body is required.");
    }

    private static Dictionary<string, JsonElement>? ToDict(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    private static ConfigChangeDto Map(ConfigChangeRow r, Caller caller)
    {
        var before = r.BeforeJson is null ? null : (JsonObject)JsonNode.Parse(r.BeforeJson)!;
        var diff = ConfigPayloadValidator.Diff(before, r.After)
            .Select(d => new ConfigDiffDto(d.Path, d.From is null ? null : JsonSerializer.Deserialize<JsonElement>(d.From.ToJsonString()), d.To is null ? null : JsonSerializer.Deserialize<JsonElement>(d.To.ToJsonString())))
            .ToList();
        var canDecide = r.Status == ConfigChangeStatuses.Pending && r.ProposerId != caller.UserId
            && (r.IsAccess ? caller.Has(Permission.AccessManage) : caller.Has(Permission.SettingsApprove));
        var a = r.Approval;
        var approval = a is null ? null : new ApprovalDto(a.Id, r.IsAccess ? "ACCESS_CHANGE" : "CONFIG_CHANGE", r.Id, null, a.ProposalVersion,
            new UserRefDto(r.ProposerId, r.ProposerName), a.ApproverId is null ? null : new UserRefDto(a.ApproverId, a.ApproverName ?? a.ApproverId),
            a.RequiredRole, a.Status, a.Decision, a.Rationale, [], a.PolicyVersion, a.ProposedAt, a.DecidedAt, canDecide);
        return new ConfigChangeDto(r.Id, r.TargetKind, r.TargetId, r.Status, true, ToDict(r.BeforeJson), ToDict(r.AfterJson)!, diff,
            new UserRefDto(r.ProposerId, r.ProposerName), r.ProposedAt, approval, r.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Reason);
    }
}
