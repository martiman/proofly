using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // Opt-in: Database:MigrateOnStartup=true applies pending migrations before the host starts serving.
        if (bool.TryParse(configuration["Database:MigrateOnStartup"], out var migrate) && migrate)
        {
            services.AddHostedService<MigrateOnStartup>();
        }
        return services;
    }
}

internal sealed class MigrateOnStartup(MigrationRunner runner) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken ct) => runner.RunAsync(ct);
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
