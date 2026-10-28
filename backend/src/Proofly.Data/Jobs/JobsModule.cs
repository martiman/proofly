using Microsoft.Extensions.DependencyInjection;

namespace Proofly.Data.Jobs;

public static class JobsModule
{
    /// <summary>Registers the queue (for enqueueing from API or worker). Requires <c>AddData</c>.</summary>
    public static IServiceCollection AddJobs(this IServiceCollection services, Action<JobOptions>? configure = null)
    {
        var options = new JobOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<JobStore>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<JobStore>());
        if (!services.Any(d => d.ServiceType == typeof(JobHandlerRegistry))) services.AddSingleton(new JobHandlerRegistry());
        services.AddSingleton<JobProcessor>();
        return services;
    }

    /// <summary>Registers <typeparamref name="THandler"/> (scoped) for job <paramref name="type"/>. Call after <c>AddJobs</c>.</summary>
    public static IServiceCollection AddJobHandler<THandler, TPayload>(this IServiceCollection services, string type)
        where THandler : class, IJobHandler<TPayload>
    {
        var registry = (JobHandlerRegistry?)services.FirstOrDefault(d => d.ServiceType == typeof(JobHandlerRegistry))?.ImplementationInstance;
        if (registry is null)
        {
            registry = new JobHandlerRegistry();
            services.AddSingleton(registry);
        }
        registry.Add<THandler, TPayload>(type);
        services.AddScoped<THandler>();
        return services;
    }
}
