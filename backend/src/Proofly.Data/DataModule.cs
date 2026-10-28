using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Proofly.Data;

public static class DataModule
{
    public static IServiceCollection AddData(this IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Proofly")
                 ?? throw new InvalidOperationException("ConnectionStrings:Proofly is not configured.");
        services.AddSingleton(new NpgsqlDataSourceBuilder(cs).Build());
        services.AddSingleton<MigrationRunner>();
        return services;
    }
}
