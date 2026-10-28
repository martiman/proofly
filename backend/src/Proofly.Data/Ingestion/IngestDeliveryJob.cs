using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Data.Jobs;

namespace Proofly.Data.Ingestion;

/// <summary>Job handler for <c>delivery.ingest</c>. Idempotent: a delivery that is already ingested is a no-op.</summary>
public sealed class IngestDeliveryJob(DeliveryIngestor ingestor) : IJobHandler<IngestDeliveryPayload>
{
    public Task HandleAsync(IngestDeliveryPayload payload, JobContext context, CancellationToken ct) =>
        ingestor.IngestAsync(payload.DeliveryId, ct);
}

public static class IngestionModule
{
    /// <summary>
    /// Registers evidence store (<c>Evidence:Directory</c>), registry, ingestor, the adapter registry (built from every
    /// <see cref="ISourceAdapter"/> in DI) and the <c>delivery.ingest</c> handler. Requires AddData and AddAudit.
    /// </summary>
    public static IServiceCollection AddIngestion(this IServiceCollection services, IConfiguration configuration, Action<IngestionOptions>? configure = null)
    {
        var evidence = new EvidenceOptions { Directory = configuration["Evidence:Directory"] ?? "" };
        var options = new IngestionOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(evidence);
        services.AddSingleton<EvidenceStore>();
        services.AddSingleton<ISourceAdapterRegistry>(sp => new SourceAdapterRegistry(sp.GetServices<ISourceAdapter>()));
        services.AddSingleton<DeliveryRegistry>();
        services.AddSingleton<DeliveryIngestor>();
        services.AddJobs();
        services.AddJobHandler<IngestDeliveryJob, IngestDeliveryPayload>(IngestionJobs.IngestDelivery);
        return services;
    }
}
