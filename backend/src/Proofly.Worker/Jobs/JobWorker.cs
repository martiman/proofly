using Proofly.Data.Jobs;

namespace Proofly.Worker.Jobs;

/// <summary>Runs <see cref="JobOptions.Concurrency"/> claim loops; stops claiming on shutdown and releases in-flight jobs.</summary>
public sealed class JobWorker(JobProcessor processor, JobOptions options) : BackgroundService
{
    private readonly string _id = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Concurrency))
            .Select(i => Task.Run(() => processor.RunAsync($"{_id}#{i}", stoppingToken), CancellationToken.None)));
}

public static class JobWorkerModule
{
    public static IServiceCollection AddJobWorker(this IServiceCollection services) =>
        services.AddJobs().AddHostedService<JobWorker>();
}
