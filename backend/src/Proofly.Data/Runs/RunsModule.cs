using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Proofly.Data.Runs;

public static class RunsDataModule
{
    public static IServiceCollection AddRunsData(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRunReadRepository>(sp => new RunReadRepository(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
