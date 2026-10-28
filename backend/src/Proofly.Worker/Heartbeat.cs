namespace Proofly.Worker;

public sealed class HeartbeatService(ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Proofly worker alive");
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}

public static class HeartbeatModule
{
    public static IServiceCollection AddHeartbeat(this IServiceCollection services) =>
        services.AddHostedService<HeartbeatService>();
}
