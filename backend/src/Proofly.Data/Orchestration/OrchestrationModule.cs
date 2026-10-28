using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Proofly.Data.Orchestration;

public static class OrchestrationDataModule
{
    /// <summary>
    /// Registers the run processor, reprocess service, enqueue helper and comparison reader. Requires AddData, AddAudit,
    /// AddJobs and IngestionOptions (AddIngestion) for the processor and enqueuer; the options default to the standard ruleset.
    /// </summary>
    public static IServiceCollection AddRunOrchestrationData(this IServiceCollection services)
    {
        services.TryAddSingleton(new RunProcessingOptions());
        services.TryAddSingleton(new Proofly.Data.Ingestion.IngestionOptions()); // AddIngestion registers the configured one
        services.TryAddSingleton<RunProcessor>();
        services.TryAddSingleton<RunReprocessService>();
        services.TryAddSingleton<RunEnqueuer>();
        services.TryAddSingleton<RunComparisonRepository>();
        return services;
    }
}
