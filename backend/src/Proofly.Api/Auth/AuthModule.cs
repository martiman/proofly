using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Proofly.Api.Platform.Http;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Auth;

public static class AuthModule
{
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IUserDirectory>(sp => new UserDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<LocalTokenService>();
        services.AddSingleton<OidcTokenValidator>();
        services.AddSingleton<ITokenValidator>(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value.Mode == AuthModes.Local
            ? sp.GetRequiredService<LocalTokenService>()
            : sp.GetRequiredService<OidcTokenValidator>());
        services.AddSingleton<ICallerResolver, CallerResolver>();
        services.AddSingleton<ICallerAccessor, HttpCallerAccessor>();
        services.AddSingleton<IStartupFilter, AuthStartupFilter>();
        services.AddHostedService<LocalUserSeeder>();
        return services;
    }

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        // x-required-permission: auth.self (every authenticated user). The contract validates this response.
        app.MapGet("/api/v1/me", (HttpContext ctx, IConfiguration cfg, IHostEnvironment host) =>
        {
            var result = HttpCallerAccessor.ResultOf(ctx) ?? throw new Platform.Problems.ProblemException(Platform.Problems.ProblemTypes.Unauthenticated);
            var c = result.Caller;
            var byEntity = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (le, perms) in c.PermissionsByLegalEntity) { byEntity[le] = perms.Order(StringComparer.Ordinal).ToList(); }
            return Results.Ok(new MeResponse(result.User.Id, result.User.Name, result.User.Email,
                c.Roles.ToList(), c.LegalEntityIds.ToList(), c.Permissions.Order(StringComparer.Ordinal).ToList(), byEntity,
                new EnvironmentDto(cfg[Observability.StartupSelfCheck.EnvironmentNameKey] is { Length: > 0 } n ? n : host.EnvironmentName,
                    cfg[Observability.StartupSelfCheck.EnvironmentKindKey] is { Length: > 0 } k ? k : "DEV")));
        });
        return app;
    }
}

/// <summary>Wire form of the contract's <c>Me</c> schema.</summary>
public sealed record MeResponse(string Id, string Name, string Email, IReadOnlyList<string> Roles, IReadOnlyList<string> LegalEntityIds,
    IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionsByLegalEntity, EnvironmentDto Environment);

/// <summary>Deployment label from configuration (Environment:Name, Environment:Kind: DEV, TEST, PILOT, PRODUCTION).</summary>
public sealed record EnvironmentDto(string Name, string Kind);

internal sealed class AuthStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<AuthenticationMiddleware>();
        next(app);
    };
}
