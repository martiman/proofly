using Proofly.Data.Evidence;
using Proofly.Data.Jobs;

namespace Proofly.Worker.Evidence;

public static class EvidenceWorkerModule
{
    /// <summary>Registers the <c>evidence_pack.generate</c> handler (and the pack builder and evidence store it needs).</summary>
    public static IServiceCollection AddEvidencePacks(this IServiceCollection services, IConfiguration configuration) =>
        services.AddEvidenceData(configuration)
            .AddJobHandler<EvidencePackJobHandler, GenerateEvidencePackPayload>(EvidencePackJobs.Generate);
}
