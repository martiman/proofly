using Proofly.Api.Auth;
using Proofly.Api.Health;
using Proofly.Api.Platform;
using Proofly.Data;
using Proofly.Data.Audit;

namespace Proofly.Api;

/// <summary>One line per module, alphabetical. Parallel agents add a line here and nowhere else in Program.cs.</summary>
public static class Modules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddAuth(configuration)
        .AddData(configuration)
        .AddAudit()
        .AddHealth()
        .AddPlatform(configuration);

    public static WebApplication MapModules(this WebApplication app)
    {
        app.UsePlatform(); // must stay first: it installs the middleware pipeline
        app.MapAuth();
        app.MapHealth();
        app.MapPlatformEndpoints();
        return app;
    }
}
