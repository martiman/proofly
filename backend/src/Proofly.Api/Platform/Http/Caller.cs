using Proofly.Api.Platform.Problems;
using Proofly.Domain.Enums;

namespace Proofly.Api.Platform.Http;

/// <summary>
/// The authenticated caller as resolved server-side. Permissions are wire forms (e.g. <c>run.view</c>), the union over all
/// roles. LegalEntityIds may contain '*' (all entities). PermissionsByLegalEntity (keys: entity id or '*') is the per-scope
/// breakdown; when empty, Permissions apply to every entity in LegalEntityIds.
/// </summary>
public sealed record Caller(string UserId, IReadOnlySet<string> Permissions, IReadOnlySet<string> LegalEntityIds)
{
    public const string AllEntities = "*";

    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> PermissionsByLegalEntity { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>();

    public bool Has(Permission permission) => Permissions.Contains(WireNames.ToPermission(permission));

    public bool InScope(string legalEntityId) => LegalEntityIds.Contains(AllEntities) || LegalEntityIds.Contains(legalEntityId);

    /// <summary>True when the caller holds the permission through a role that covers this legal entity.</summary>
    public bool Can(Permission permission, string legalEntityId)
    {
        if (!InScope(legalEntityId)) { return false; }
        var wire = WireNames.ToPermission(permission);
        if (PermissionsByLegalEntity.Count == 0) { return Permissions.Contains(wire); }
        return (PermissionsByLegalEntity.TryGetValue(legalEntityId, out var p) && p.Contains(wire))
            || (PermissionsByLegalEntity.TryGetValue(AllEntities, out var all) && all.Contains(wire));
    }

    /// <summary>Throws 403 out-of-scope when the entity is not in the caller's scope. Use <see cref="RequireScopeOrNotFound"/> where the contract says not to leak existence.</summary>
    public void RequireScope(string legalEntityId)
    {
        if (!InScope(legalEntityId)) { throw new ProblemException(ProblemTypes.OutOfScope, "The legal entity is outside the caller's scope."); }
    }

    /// <summary>Throws 404 (not 403) when the entity is out of scope, so existence is not disclosed.</summary>
    public void RequireScopeOrNotFound(string legalEntityId)
    {
        if (!InScope(legalEntityId)) { throw new ProblemException(ProblemTypes.NotFound); }
    }

    /// <summary>Scope plus permission for one entity: 404 when out of scope, 403 when in scope but the role there lacks the permission.</summary>
    public void Require(Permission permission, string legalEntityId)
    {
        RequireScopeOrNotFound(legalEntityId);
        if (!Can(permission, legalEntityId)) { throw new ProblemException(ProblemTypes.Forbidden, "The caller lacks the required permission for this legal entity."); }
    }
}

/// <summary>Seam for authentication (OIDC, issue-specific). The default accessor treats everyone as unauthenticated (401).</summary>
public interface ICallerAccessor
{
    Caller? GetCaller(HttpContext ctx);
}

public sealed class AnonymousCallerAccessor : ICallerAccessor
{
    public Caller? GetCaller(HttpContext ctx) => null;
}

/// <summary>Endpoint metadata for endpoints that are not in the OpenAPI contract. Contract operations use x-required-permission.</summary>
public sealed record RequiredPermissionMetadata(Permission Permission);

public static class CallerExtensions
{
    internal const string ItemKey = "proofly.caller";

    /// <summary>The caller authenticated and authorised by the platform for this request. Throws 401 if none.</summary>
    public static Caller GetCaller(this HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemKey, out var c) && c is Caller caller ? caller : throw new ProblemException(ProblemTypes.Unauthenticated);

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredPermissionMetadata(permission));
}
