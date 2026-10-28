using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Common;
using Proofly.Contracts.Governance;
using Proofly.Data.Audit;
using Proofly.Data.Governance;
using Proofly.Domain.Enums;

namespace Proofly.Api.Governance;

public static class GovernanceModule
{
    public static IServiceCollection AddGovernance(this IServiceCollection services)
    {
        services.AddGovernanceData();
        services.AddSingleton<IEndpointModule, GovernanceEndpoints>();
        return services;
    }
}

internal sealed class GovernanceEndpoints : IEndpointModule
{
    /// <summary>Null = every legal entity (wildcard scope).</summary>
    private static string[]? Scope(Caller c) => c.LegalEntityIds.Contains(Caller.AllEntities) ? null : c.LegalEntityIds.ToArray();

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        g.MapGet("/audit-events", async (HttpContext ctx, IAuditReader reader, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var q = ctx.Request.Query;
            string? S(string k) => q.TryGetValue(k, out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;
            DateTimeOffset? D(string k) => S(k) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
            ActorType? at = S("actorType") is { } a && Enum.TryParse<ActorType>(a.Replace("_", ""), true, out var parsed) ? parsed : null;
            var page = PageRequest.From(ctx);
            string? cursor = page.Cursor;
            AuditPage res;
            try
            {
                res = await reader.ListAsync(new AuditQuery(S("runId"), S("subjectKind"), S("subjectId"), S("type"), S("actorId"), at, D("from"), D("to"),
                    S("search"), page.Limit, cursor, Scope(caller)), ct);
            }
            catch (ArgumentException ex) when (ex.ParamName == "cursor") { throw ProblemException.Validation("cursor", "The cursor is not valid."); }
            return Results.Ok(new PageDto<AuditEventDto>(res.Items.Select(Map).ToList(), new PageInfoDto(res.NextCursor, res.HasMore)));
        });

        g.MapGet("/settings", async (HttpContext ctx, ISettingsRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var requested = ctx.Request.Query.TryGetValue("legalEntityId", out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;
            if (requested is not null) { caller.Require(Permission.SettingsView, requested); }
            var snap = await repo.GetAsync(requested, Scope(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            if (requested is null) { caller.Require(Permission.SettingsView, snap.LegalEntity.Id); }
            return Results.Ok(new SettingsDto(
                new LegalEntityDto(snap.LegalEntity.Id, snap.LegalEntity.Name, snap.LegalEntity.Currencies),
                snap.Rules.Select(r => new RuleConfigDto(r.RuleId, r.Control, r.Version, r.Description, r.Tolerance, r.Severity, r.EffectiveFrom, r.PendingChangeId)).ToList(),
                snap.Sources.Select(x => new SourceConfigDto(x.SourceName, x.SchemaVersion, x.ExpectedByLocalTime, x.Mandatory, x.PendingChangeId)).ToList(),
                snap.Users.Select(u => new UserAccessDto(u.Id, u.Name, u.Email, u.Roles, u.LegalEntityIds, u.PendingChangeId)).ToList(),
                snap.FreshnessPolicy,
                snap.ApprovalPolicies.Select(p => new ApprovalPolicyDto(p.Name, p.Description, p.RequiredRole, p.Version)).ToList(),
                snap.PendingChanges));
        });

        g.MapGet("/approvals", async (HttpContext ctx, IApprovalRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var page = PageRequest.From(ctx);
            var q = ctx.Request.Query;
            var type = q.TryGetValue("objectType", out var t) && t.Count > 0 && t[0] is { Length: > 0 } ts ? ts : null;
            var mine = !(q.TryGetValue("mine", out var m) && bool.TryParse(m, out var mb) && !mb);
            var rows = await repo.ListAsync(new ApprovalQuery(caller.UserId, Scope(caller), type, mine, page.Limit, page.Decode<ApprovalCursor>()), ct);
            var more = rows.HasMore;
            var items = rows.Items.Select(Map).ToList();
            var next = more ? Cursors.Encode(new ApprovalCursor(rows.Items[^1].ProposedAt, rows.Items[^1].Id)) : null;
            return Results.Ok(new PageDto<ApprovalDto>(items, new PageInfoDto(next, more)));
        });

        g.MapGet("/runs/{runId}/evidence-pack", async (string runId, HttpContext ctx, IEvidencePackRepository repo, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var p = await repo.GetForRunAsync(runId, Scope(caller), ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            return Results.Ok(new EvidencePackDto(p.Id, p.RunId, p.GeneratedAt, p.Status, p.Contents, p.Sha256, p.SizeBytes, p.FailureReason,
                p.ManifestJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<EvidenceManifestDto>(p.ManifestJson, Platform.Json.WireJson.Options),
                p.AuditChainJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<AuditChainDto>(p.AuditChainJson, Platform.Json.WireJson.Options)));
        });
    }

    private static AuditEventDto Map(AuditEventRecord e) => new(e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), e.At,
        new ActorDto(WireNames.ToUpperSnake(e.ActorType), e.ActorId, string.IsNullOrEmpty(e.ActorName) ? null : e.ActorName), e.Type, e.RunId, e.SubjectKind, e.SubjectId,
        e.Summary, e.Reason, e.RuleVersion, e.RequestId, e.Hash, e.PreviousHash);

    private static ApprovalDto Map(ApprovalRow a) => new(a.Id, a.ObjectType, a.ObjectId, a.RunId, a.ProposalVersion,
        new UserRefDto(a.Proposer.Id, a.Proposer.Name), a.Approver is null ? null : new UserRefDto(a.Approver.Id, a.Approver.Name),
        a.RequiredRole, a.Status, a.Decision, a.Rationale, a.EvidenceRefs, a.PolicyVersion, a.ProposedAt, a.DecidedAt, a.CanDecide, a.Kind);
}
