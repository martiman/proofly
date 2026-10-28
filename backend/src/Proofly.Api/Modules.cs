using Proofly.Api.Health;
using Proofly.Data;

namespace Proofly.Api;

/// <summary>One line per module, alphabetical. Parallel agents add a line here and nowhere else in Program.cs.</summary>
public static class Modules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddData(configuration)
        .AddHealth();

    public static WebApplication MapModules(this WebApplication app)
    {
        app.MapHealth();
        return app;
    }
}
