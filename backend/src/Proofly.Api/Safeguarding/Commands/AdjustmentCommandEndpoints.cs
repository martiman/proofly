using System.Globalization;
using System.Text.Json.Nodes;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Data.Safeguarding;
using Proofly.Data.Safeguarding.Commands;
using Proofly.Domain.Enums;

namespace Proofly.Api.Safeguarding.Commands;

public static class SafeguardingCommandsModule
{
    public static IServiceCollection AddSafeguardingCommands(this IServiceCollection services)
    {
        services.AddSafeguardingCommandsData();
        services.AddSingleton<IEndpointModule, AdjustmentCommandEndpoints>();
        return services;
    }
}

/// <summary>proposeAdjustment and decideAdjustment. Permissions by x-required-permission (platform) plus per-entity checks here.</summary>
internal sealed class AdjustmentCommandEndpoints(IAdjustmentCommands commands, ISafeguardingReadRepository reads) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/runs/{runId}/safeguarding/adjustments", Propose);
        app.MapPost("/api/v1/safeguarding/adjustments/{adjustmentId}/decision", Decide);
    }

    private static EntityScope ScopeOf(Caller c) => c.LegalEntityIds.Contains(Caller.AllEntities) ? EntityScope.Everything : new EntityScope(false, c.LegalEntityIds.ToArray());

    private async Task<IResult> Propose(string runId, HttpContext ctx, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var entity = await commands.FindRunEntityAsync(runId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.SafeguardingAdjustmentPropose, entity);

        var body = ctx.Feature().Body as JsonObject ?? throw ProblemException.Validation("", "A JSON request body is required.");
        var currency = body["currency"]!.GetValue<string>();
        var money = body["amount"] as JsonObject ?? throw ProblemException.Validation("/amount", "amount is required.");
        if (money["currency"]!.GetValue<string>() != currency) { throw ProblemException.Validation("/amount/currency", "amount.currency must equal currency."); }
        if (!decimal.TryParse(money["amount"]!.GetValue<string>(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            throw ProblemException.Validation("/amount/amount", "amount is not a decimal string.");
        }
        if (amount == 0m) { throw ProblemException.Validation("/amount/amount", "amount must not be zero."); }
        if (amount != decimal.Round(amount, 6)) { throw ProblemException.Validation("/amount/amount", "amount has more than 6 decimal places; it is rejected, not rounded."); }
        var reason = body["reason"]!.GetValue<string>().Trim();
        if (reason.Length < 3) { throw ProblemException.Validation("/reason", "reason needs at least 3 characters."); }
        var refs = body["evidenceRefs"]!.AsArray().Select(n => n!.GetValue<string>().Trim()).ToList();
        if (refs.Count == 0 || refs.Any(r => r.Length == 0)) { throw ProblemException.Validation("/evidenceRefs", "At least one non-blank evidence reference is required."); }
        DateOnly? expires = null;
        if (body["expiresOn"] is JsonValue ev)
        {
            if (!DateOnly.TryParseExact(ev.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { throw ProblemException.Validation("/expiresOn", "expiresOn must be a date (yyyy-MM-dd)."); }
            expires = d;
        }

        var result = await commands.ProposeAsync(new ProposeAdjustmentCommand(runId, caller.UserId, currency, amount, reason, refs, expires, ctx.GetRequestId()), ct);
        Throw(result);
        var row = await Load(runId, result.AdjustmentId!, caller, ct);
        ctx.Set(ETags.ForVersion(row.Version));
        return Results.Created($"/api/v1/runs/{runId}/safeguarding/adjustments?status=PROPOSED", SafeguardingEndpoints.Map(row, caller, entity));
    }

    private async Task<IResult> Decide(string adjustmentId, HttpContext ctx, CancellationToken ct)
    {
        var caller = ctx.GetCaller();
        var head = await commands.FindAdjustmentAsync(adjustmentId, ScopeOf(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
        caller.Require(Permission.SafeguardingAdjustmentApprove, head.LegalEntityId);
        ctx.RequireIfMatch(ETags.ForVersion(head.Version));

        var body = ctx.Feature().Body as JsonObject ?? throw ProblemException.Validation("", "A JSON request body is required.");
        var reason = body["reason"]!.GetValue<string>().Trim();
        if (reason.Length < 3) { throw ProblemException.Validation("/reason", "reason needs at least 3 characters."); }
        var approve = body["decision"]!.GetValue<string>() == "APPROVE";
        int? pv = body["proposalVersion"] is JsonValue p ? p.GetValue<int>() : null;

        var result = await commands.DecideAsync(new DecideAdjustmentCommand(adjustmentId, caller.UserId, approve, reason, head.Version, pv, ctx.GetRequestId()), ct);
        Throw(result);
        var row = await Load(head.RunId, adjustmentId, caller, ct);
        ctx.Set(ETags.ForVersion(row.Version));
        return Results.Ok(SafeguardingEndpoints.Map(row, caller, head.LegalEntityId));
    }

    private async Task<AdjustmentRow> Load(string runId, string id, Caller caller, CancellationToken ct) =>
        (await reads.ListAdjustmentsAsync(runId, null, ScopeOf(caller), ct))?.FirstOrDefault(a => a.Id == id) ?? throw new ProblemException(ProblemTypes.NotFound);

    private static void Throw(AdjustmentCommandResult r)
    {
        switch (r.Outcome)
        {
            case CommandOutcome.Ok: return;
            case CommandOutcome.NotFound: throw new ProblemException(ProblemTypes.NotFound);
            case CommandOutcome.PreconditionFailed: throw new ProblemException(ProblemTypes.PreconditionFailed, "The adjustment changed since it was read; re-read it and retry.");
            case CommandOutcome.SeparationOfDuties: throw new ProblemException(ProblemTypes.SeparationOfDuties, r.Detail);
            case CommandOutcome.StaleProposal: throw new ProblemException(ProblemTypes.StaleProposal, r.Detail);
            case CommandOutcome.RunLocked: throw new ProblemException(ProblemTypes.InvalidState, r.Detail);
            case CommandOutcome.RunSuperseded: throw new ProblemException(ProblemTypes.RunSuperseded, r.Detail);
            case CommandOutcome.Validation: throw ProblemException.Validation(r.Pointer ?? "", r.Detail ?? "Invalid input.");
            default: throw new ProblemException(ProblemTypes.InvalidState, r.Detail);
        }
    }
}
