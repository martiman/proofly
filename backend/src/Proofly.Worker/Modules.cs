using Proofly.Data;

namespace Proofly.Worker;

/// <summary>One line per module, alphabetical.</summary>
public static class Modules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddData(configuration)
        .AddHeartbeat();
}
