using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proofly.Api.Platform.Http;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Auth;

/// <summary>Fixed users, one per role, for development and evaluation (Auth:Mode=Local). Scope is all legal entities.</summary>
public static class LocalUsers
{
    public static string Subject(Role role) => "local:" + WireNames.ToUpperSnake(role).ToLowerInvariant();

    public static UserRecord For(Role role)
    {
        var key = WireNames.ToUpperSnake(role).ToLowerInvariant();
        return new UserRecord("local-" + key.Replace('_', '-'), "Local " + role, key.Replace('_', '.') + "@local.proofly.test", Subject(role), true,
            [new RoleAssignment(WireNames.ToUpperSnake(role), Caller.AllEntities)]);
    }

    public static IEnumerable<UserRecord> All => RolePermissions.Roles.Select(For);
}

/// <summary>Seeds the fixed local users at startup, only in Local mode. Optionally logs tokens (Auth:Local:LogTokens).</summary>
internal sealed class LocalUserSeeder(IUserDirectory users, LocalTokenService tokens, IOptions<AuthOptions> options, ILogger<LocalUserSeeder> log) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken ct)
    {
        if (options.Value.Mode != AuthModes.Local) { return; }
        log.LogWarning("Auth:Mode=Local is active: fixed evaluation users and locally signed tokens. Never use in production.");
        foreach (var u in LocalUsers.All)
        {
            await users.EnsureUserAsync(u, ct);
            if (options.Value.Local.LogTokens) { log.LogWarning("Local token for {User} ({Roles}): {Token}", u.Id, string.Join(",", u.Assignments.Select(a => a.RoleId)), tokens.Issue(u.ExternalSubject!)); }
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
