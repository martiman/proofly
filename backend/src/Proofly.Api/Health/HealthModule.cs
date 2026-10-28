namespace Proofly.Api.Health;

public static class HealthModule
{
    public static IServiceCollection AddHealth(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        return app;
    }
}
