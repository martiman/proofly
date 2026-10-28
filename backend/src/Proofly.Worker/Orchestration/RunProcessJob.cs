using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;

namespace Proofly.Worker.Orchestration;

/// <summary>
/// Job handler for <c>run.process</c>. Idempotent: a run that already has a result is left alone. When the last attempt
/// fails the run is marked FAILED (processing is one transaction, so no partial results exist) and the failure is rethrown
/// so the job is dead-lettered.
/// </summary>
public sealed class RunProcessJob(RunProcessor processor, ILogger<RunProcessJob> logger) : IJobHandler<ProcessRunPayload>
{
    public async Task HandleAsync(ProcessRunPayload payload, JobContext context, CancellationToken ct)
    {
        try
        {
            var outcome = await processor.ProcessAsync(payload, ct);
            if (outcome.Status == RunProcessingStatus.NotFound) throw new PermanentJobException($"Run {payload.RunId} does not exist.");
            logger.LogInformation("run.process {Status} run {RunId} state {State} exceptions {Exceptions}", outcome.Status, outcome.RunId, outcome.State, outcome.ExceptionCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ex is PermanentJobException || context.Attempt >= context.MaxAttempts))
        {
            await processor.MarkFailedAsync(payload, ex.Message, CancellationToken.None);
            throw;
        }
    }
}

public static class RunProcessingWorkerModule
{
    /// <summary>Registers the <c>run.process</c> handler. Needs AddData, AddAudit and AddIngestion (for the ingestion options); it registers the orchestration services and jobs itself.</summary>
    public static IServiceCollection AddRunProcessing(this IServiceCollection services)
    {
        services.AddJobs();
        services.AddRunOrchestrationData();
        services.AddJobHandler<RunProcessJob, ProcessRunPayload>(RunJobs.ProcessRun);
        return services;
    }
}
