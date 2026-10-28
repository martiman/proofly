using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Proofly.Data.Jobs;
using Proofly.Data.Safeguarding.Commands;

namespace Proofly.Worker.Safeguarding;

/// <summary>
/// Minimal scheduler hook: on start and then hourly, enqueues <c>safeguarding.adjustment.expire</c> for today's (UTC) date.
/// The dedupe key makes it one job per day however many workers or ticks run. Business date = UTC calendar date (no per-entity calendar yet).
/// </summary>
public sealed class AdjustmentExpiryScheduler(IJobQueue queue, TimeProvider time, ILogger<AdjustmentExpiryScheduler> log) : BackgroundService
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Could not enqueue the adjustment expiry job"); }
            try { await Task.Delay(Interval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task<EnqueueResult> TickAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return queue.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload(today), DedupeKey: $"{AdjustmentCommands.ExpireJobType}:{today}"), null, ct);
    }
}

public static class AdjustmentExpiryModule
{
    public static IServiceCollection AddAdjustmentExpiry(this IServiceCollection services)
    {
        services.AddSafeguardingCommandsData();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<AdjustmentExpiryScheduler>();
        return services.AddJobHandler<ExpireAdjustmentsHandler, ExpireAdjustmentsPayload>(AdjustmentCommands.ExpireJobType);
    }
}
