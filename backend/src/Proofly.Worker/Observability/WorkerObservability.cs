using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;

namespace Proofly.Worker.Observability;

public static class WorkerSelfCheck
{
    public static IReadOnlyList<string> Validate(IConfiguration c, bool isProduction, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.GetConnectionString("Proofly"))) { errors.Add("ConnectionStrings:Proofly is not configured."); }
        var evidence = c["Evidence:Directory"];
        if (string.IsNullOrWhiteSpace(evidence)) { if (isProduction) { errors.Add("Evidence:Directory is not configured."); } }
        else if (!directoryExists(evidence)) { errors.Add($"Evidence:Directory '{evidence}' does not exist."); }
        return errors;
    }
}

/// <summary>Correlation helper for job handlers: every log line inside the scope carries RunId.</summary>
public static class RunLogScope
{
    public static IDisposable Begin(Guid runId) => LogContext.PushProperty("RunId", runId);
}

public static class WorkerObservabilityModule
{
    public static HostApplicationBuilder AddObservability(this HostApplicationBuilder builder)
    {
        var errors = WorkerSelfCheck.Validate(builder.Configuration, builder.Environment.IsProduction());
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }

        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(o => o.ServiceName = "Proofly.Worker");
        builder.Services.AddSerilog((_, lc) => lc
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "proofly-worker")
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        // Metrics: off by default; when enabled, Prometheus text on a loopback-only HTTP listener.
        var m = builder.Configuration.GetSection("Observability:Metrics");
        if (m.GetValue<bool>("Enabled"))
        {
            var prefix = m["ListenerPrefix"] ?? "http://127.0.0.1:9465/";
            builder.Services.AddOpenTelemetry().WithMetrics(x => x
                .AddMeter("Proofly.Jobs")
                .AddPrometheusHttpListener(o => o.ConfigureHttpListener = (_, l) => { l.Prefixes.Clear(); l.Prefixes.Add(prefix); }));
        }
        return builder;
    }
}
