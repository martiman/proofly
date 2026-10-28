using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Proofly.Data.Sources;

public static class SourcesDataModule
{
    public static IServiceCollection AddSourceReads(this IServiceCollection services) =>
        services.AddSingleton<ISourceReadRepository>(sp => new SourceReadRepository(sp.GetRequiredService<NpgsqlDataSource>()));
}
