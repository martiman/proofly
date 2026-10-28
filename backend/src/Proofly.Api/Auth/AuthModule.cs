using System.Text.Json.Nodes;
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
        app.MapGet("/api/v1/me", (HttpContext ctx) =>
        {
            var result = HttpCallerAccessor.ResultOf(ctx) ?? throw new Platform.Problems.ProblemException(Platform.Problems.ProblemTypes.Unauthenticated);
            var c = result.Caller;
            var byEntity = new JsonObject();
            foreach (var (le, perms) in c.PermissionsByLegalEntity.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                byEntity[le] = new JsonArray(perms.Order(StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            }
            return Results.Ok(new MeResponse(result.User.Id, result.User.Name, result.User.Email,
                c.Roles.ToList(), c.LegalEntityIds.ToList(), c.Permissions.Order(StringComparer.Ordinal).ToList(), byEntity));
        });
        return app;
    }
}

/// <summary>Wire form of the contract's <c>Me</c> schema.</summary>
public sealed record MeResponse(string Id, string Name, string Email, IReadOnlyList<string> Roles, IReadOnlyList<string> LegalEntityIds,
    IReadOnlyList<string> Permissions, JsonObject PermissionsByLegalEntity);

internal sealed class AuthStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<AuthenticationMiddleware>();
        next(app);
    };
}
