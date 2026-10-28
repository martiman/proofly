using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;
using Proofly.Api.Platform.Http;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Auth;

/// <summary>The outcome of authenticating a request: the local user and the caller built from the database roles.</summary>
public sealed record AuthResult(UserRecord User, Caller Caller, DateTimeOffset SessionExpiresAt);

public interface ICallerResolver
{
    Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct);
}

/// <summary>Maps a validated token (sub) to the local users row and its role assignments. Unknown or inactive users are not authenticated.</summary>
public sealed class CallerResolver(ITokenValidator validator, IUserDirectory users, IOptions<AuthOptions> options, IAuthAuditHook? audit = null) : ICallerResolver
{
    public async Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct)
    {
        var token = await validator.ValidateAsync(bearerToken, ct);
        if (token is null) { return null; }

        var user = await users.FindBySubjectAsync(token.Subject, ct);
        if (user is null && options.Value.Mode == AuthModes.Oidc && options.Value.Oidc.ProvisionOnFirstLogin && audit is not null
            && !string.IsNullOrWhiteSpace(token.Email))
        {
            user = await users.ProvisionAsync(token.Subject, token.Name ?? token.Email, token.Email, audit, ct);
        }
        if (user is null || !user.Active) { return null; }
        return new AuthResult(user, Build(user), token.ExpiresAt);
    }

    /// <summary>Pure mapping from assignments to effective roles, permissions and scope. Unknown role ids grant nothing.</summary>
    public static Caller Build(UserRecord user)
    {
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        var perms = new HashSet<string>(StringComparer.Ordinal);
        var scope = new SortedSet<string>(StringComparer.Ordinal);
        var byEntity = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var a in user.Assignments)
        {
            if (!RolePermissions.TryParseRole(a.RoleId, out var role)) { continue; }
            roles.Add(a.RoleId);
            scope.Add(a.LegalEntityId);
            if (!byEntity.TryGetValue(a.LegalEntityId, out var set)) { byEntity[a.LegalEntityId] = set = new HashSet<string>(StringComparer.Ordinal); }
            foreach (var p in RolePermissions.For(role))
            {
                var wire = WireNames.ToPermission(p);
                perms.Add(wire);
                set.Add(wire);
            }
        }
        return new Caller(user.Id, perms, scope)
        {
            Roles = roles,
            PermissionsByLegalEntity = byEntity.ToDictionary(k => k.Key, k => (IReadOnlySet<string>)k.Value, StringComparer.Ordinal),
        };
    }
}

/// <summary>Reads the result of <see cref="AuthenticationMiddleware"/>; synchronous because the lookup already happened.</summary>
public sealed class HttpCallerAccessor : ICallerAccessor
{
    internal const string ResultKey = "proofly.auth.result";
    internal const string FailureKey = "proofly.auth.failure";

    public Caller? GetCaller(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(FailureKey, out var f) && f is Exception ex) { ExceptionDispatchInfo.Capture(ex).Throw(); }
        return ctx.Items.TryGetValue(ResultKey, out var r) && r is AuthResult a ? a.Caller : null;
    }

    public static AuthResult? ResultOf(HttpContext ctx) => ctx.Items.TryGetValue(ResultKey, out var r) ? r as AuthResult : null;
}

/// <summary>
/// Runs before the platform pipeline: validates the bearer token and loads the user asynchronously, then stores the result.
/// An invalid or expired token yields no caller (401 from AccessMiddleware); an infrastructure failure is rethrown there as 500.
/// The session lasts exactly as long as the token: nothing is cached across requests.
/// </summary>
public sealed class AuthenticationMiddleware(RequestDelegate next, ICallerResolver resolver)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (await resolver.ResolveAsync(header[scheme.Length..].Trim(), ctx.RequestAborted) is { } result)
                {
                    ctx.Items[HttpCallerAccessor.ResultKey] = result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Items[HttpCallerAccessor.FailureKey] = ex;
            }
        }
        await next(ctx);
    }
}
