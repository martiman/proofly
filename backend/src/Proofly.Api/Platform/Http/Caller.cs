using Proofly.Api.Platform.Problems;
using Proofly.Domain.Enums;

namespace Proofly.Api.Platform.Http;

/// <summary>The authenticated caller as resolved server-side (roles, legal-entity scope, effective permissions).</summary>
public sealed record Caller(string UserId, IReadOnlySet<string> Permissions, IReadOnlySet<string> LegalEntityIds)
{
    public bool Has(Permission permission) => Permissions.Contains(WireNames.ToPermission(permission));

    /// <summary>Throws 403 out-of-scope when the entity is not in the caller's scope. Use 404 instead where the contract says not to leak existence.</summary>
    public void RequireScope(string legalEntityId)
    {
        if (!LegalEntityIds.Contains(legalEntityId)) { throw new ProblemException(ProblemTypes.OutOfScope, "The legal entity is outside the caller's scope."); }
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
