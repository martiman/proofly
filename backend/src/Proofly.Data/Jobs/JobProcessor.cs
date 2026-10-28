using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Proofly.Data.Jobs;

/// <summary>Maps job type to a payload type and handler. Populated by <c>AddJobHandler</c>.</summary>
public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, string, JobContext, CancellationToken, Task>> _handlers = new();

    public void Add<THandler, TPayload>(string type) where THandler : class, IJobHandler<TPayload>
    {
        if (!_handlers.TryAdd(type, async (sp, json, ctx, ct) =>
            {
                var payload = JsonSerializer.Deserialize<TPayload>(json, JobJson.Options)
                              ?? throw new PermanentJobException($"Payload for '{type}' is null.");
                await sp.GetRequiredService<THandler>().HandleAsync(payload, ctx, ct);
            }))
            throw new InvalidOperationException($"A handler for job type '{type}' is already registered.");
    }

    internal bool TryGet(string type, out Func<IServiceProvider, string, JobContext, CancellationToken, Task> h) => _handlers.TryGetValue(type, out h!);
}

/// <summary>
/// Claims and runs jobs with a lease + heartbeat. Retries with exponential backoff, dead-letters when attempts run out.
/// A failure is recorded in the jobs table; a lost lease (crash elsewhere) cancels the handler.
/// </summary>
public sealed class JobProcessor(JobStore store, JobHandlerRegistry registry, IServiceScopeFactory scopes, JobOptions options, ILogger<JobProcessor> logger)
{
    private static readonly Meter Meter = new("Proofly.Jobs");
    private static readonly Counter<long> Succeeded = Meter.CreateCounter<long>("proofly.jobs.succeeded");
    private static readonly Counter<long> Failed = Meter.CreateCounter<long>("proofly.jobs.failed");
    private static readonly Counter<long> Dead = Meter.CreateCounter<long>("proofly.jobs.dead");

    /// <summary>Poll loop. Returns when <paramref name="stop"/> is cancelled; an in-flight job is released without losing an attempt.</summary>
    public async Task RunAsync(string workerId, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await ProcessOneAsync(workerId, stop)) continue;
                await Task.Delay(options.PollInterval, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job loop error; retrying");
                try { await Task.Delay(options.PollInterval, stop); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>Recover expired leases, then claim and run at most one job. Returns false if nothing was ready.</summary>
    public async Task<bool> ProcessOneAsync(string workerId, CancellationToken stop)
    {
        await store.RecoverExpiredLeasesAsync(stop);
        var job = await store.ClaimAsync(workerId, options.LeaseDuration, stop);
        if (job is null) return false;

        using var lost = new CancellationTokenSource();
        using var hct = CancellationTokenSource.CreateLinkedTokenSource(stop, lost.Token);
        using var beatStop = new CancellationTokenSource();
        var beat = HeartbeatLoop(job.Id, workerId, lost, beatStop.Token);
        string? error = null;
        var permanent = false;
        try
        {
            if (!registry.TryGet(job.Type, out var run))
            {
                error = $"No handler registered for job type '{job.Type}'.";
            }
            else
            {
                await using var scope = scopes.CreateAsyncScope();
                await run(scope.ServiceProvider, job.PayloadJson,
                    new JobContext(job.Id, job.Attempts, job.MaxAttempts, job.RunId, job.DeliveryId), hct.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await StopBeat(beat, beatStop);
            await store.ReleaseAsync(job.Id, workerId, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            permanent = ex is PermanentJobException or JsonException;
        }
        await StopBeat(beat, beatStop);

        if (lost.IsCancellationRequested)
        {
            logger.LogWarning("Job {Id} lost its lease; result discarded", job.Id);
            return true;
        }
        if (error is null)
        {
            if (await store.CompleteAsync(job.Id, workerId, CancellationToken.None)) Succeeded.Add(1);
            return true;
        }
        var state = await store.FailAsync(job.Id, workerId, error, options.BackoffFor(job.Attempts), permanent, CancellationToken.None);
        logger.LogWarning("Job {Id} ({Type}) attempt {Attempt}/{Max} failed -> {State}: {Error}", job.Id, job.Type, job.Attempts, job.MaxAttempts, state, error);
        (state == JobState.Dead ? Dead : Failed).Add(1);
        return true;
    }

    private async Task StopBeat(Task beat, CancellationTokenSource beatStop)
    {
        await beatStop.CancelAsync();
        try { await beat; } catch (OperationCanceledException) { }
    }

    /// <summary>Extends the lease at a third of its duration until cancelled; cancels the handler if the lease is lost.</summary>
    private async Task HeartbeatLoop(long id, string worker, CancellationTokenSource lost, CancellationToken beatStop)
    {
        var interval = options.LeaseDuration / 3;
        try
        {
            while (true)
            {
                await Task.Delay(interval, beatStop);
                if (!await store.HeartbeatAsync(id, worker, options.LeaseDuration, beatStop))
                {
                    await lost.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Heartbeat for job {Id} failed", id); }
    }
}
