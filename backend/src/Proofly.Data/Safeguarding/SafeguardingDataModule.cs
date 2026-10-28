using Microsoft.Extensions.DependencyInjection;

namespace Proofly.Data.Safeguarding;

public static class SafeguardingDataModule
{
    public static IServiceCollection AddSafeguardingData(this IServiceCollection services) =>
        services.AddSingleton<ISafeguardingReadRepository, SafeguardingReadRepository>();
}
