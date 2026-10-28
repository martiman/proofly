using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Proofly.Api.Platform.Contract;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Idempotency;
using Proofly.Api.Platform.Json;

namespace Proofly.Api.Platform;

public sealed class PlatformOptions
{
    public const string Section = "Platform";
    /// <summary>Validate responses against the contract. A violation returns 500 and logs; keep on (it catches contract drift).</summary>
    public bool ValidateResponses { get; set; } = true;
    public TimeSpan IdempotencyTtl { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Extra OpenAPI documents (tests only). The main contract is embedded from docs/integrations/api/openapi.yaml.</summary>
    public List<string> AdditionalContractYaml { get; } = [];
}

/// <summary>Endpoint groups register themselves through this so Program.cs never changes.</summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder app);
}

public static class PlatformModule
{
    private const string CorsPolicy = "proofly-web";
    private static readonly string[] DefaultOrigins = ["http://localhost:5173", "http://127.0.0.1:5173"];

    public static IServiceCollection AddPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.Section));
        services.Configure<JsonOptions>(o => WireJson.Configure(o.SerializerOptions));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.TryAddSingleton<ICallerAccessor, AnonymousCallerAccessor>();
        services.AddSingleton(sp => OpenApiContract.LoadEmbedded(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlatformOptions>>().Value.AdditionalContractYaml));

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() is { Length: > 0 } o ? o : DefaultOrigins;
        services.AddCors(c => c.AddPolicy(CorsPolicy, p => p
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "If-Match", "If-None-Match", "Idempotency-Key", "X-Request-Id")
            .WithExposedHeaders("ETag", "X-Request-Id", "Idempotent-Replayed", "Location", "WWW-Authenticate")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        return services;
    }

    /// <summary>
    /// Pipeline: request id -> problem handler -> routing -> CORS -> access (401/403/428) -> request validation (422)
    /// -> idempotency -> response validation -> endpoint. Called first from MapModules.
    /// </summary>
    public static WebApplication UsePlatform(this WebApplication app)
    {
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<ProblemExceptionMiddleware>();
        app.UseRouting();
        app.UseCors(CorsPolicy);
        app.UseMiddleware<AccessMiddleware>();
        app.UseMiddleware<RequestValidationMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        app.UseMiddleware<ResponseValidationMiddleware>();
        return app;
    }

    public static WebApplication MapPlatformEndpoints(this WebApplication app)
    {
        foreach (var module in app.Services.GetServices<IEndpointModule>()) { module.Map(app); }
        return app;
    }
}
