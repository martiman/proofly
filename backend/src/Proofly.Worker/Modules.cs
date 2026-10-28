using Proofly.Data;
using Proofly.Worker.Jobs;
using Proofly.Data.Audit;

namespace Proofly.Worker;

/// <summary>One line per module, alphabetical.</summary>
public static class Modules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddData(configuration)
        .AddAudit()
        .AddHeartbeat()
        .AddJobWorker();
}
