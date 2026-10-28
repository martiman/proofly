using Proofly.Connectors.Csv;
using Proofly.Connectors.Excel;
using Proofly.Connectors.Xml;
using Proofly.Data;
using Proofly.Data.Ingestion;
using Proofly.Worker.Evidence;
using Proofly.Worker.Jobs;
using Proofly.Worker.Orchestration;
using Proofly.Worker.Safeguarding;
using Proofly.Data.Audit;

namespace Proofly.Worker;

/// <summary>One line per module, alphabetical.</summary>
public static class Modules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddData(configuration)
        .AddAudit()
        .AddAdjustmentExpiry()
        .AddCsvAdapters()
        .AddEvidencePacks(configuration)
        .AddExcelAdapters()
        .AddHeartbeat()
        .AddIngestion(configuration)
        .AddJobWorker()
        .AddRunProcessing()
        .AddXmlAdapters();
}
