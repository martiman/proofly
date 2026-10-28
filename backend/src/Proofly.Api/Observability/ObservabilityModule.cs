using System.Net;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;

namespace Proofly.Api.Observability;

public static class ObservabilityModule
{
    /// <summary>Host-level wiring: fail-fast self-check, JSON logs, service-manager integration, metrics.</summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        StartupSelfCheck.ThrowIfInvalid(builder.Configuration, builder.Environment.IsProduction());

        builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false); // no server software banner
        builder.Host.UseSystemd();
        builder.Host.UseWindowsService();
        builder.Host.UseSerilog((ctx, lc) => lc
            .ReadFrom.Configuration(ctx.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "proofly-api")
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        builder.Services.AddSingleton<IReadinessCheck, DatabaseReadiness>();
        builder.Services.AddSingleton<IReadinessCheck, MigrationsReadiness>();
        builder.Services.AddSingleton<IReadinessCheck, EvidenceDirectoryReadiness>();

        var options = MetricsOptions.From(builder.Configuration);
        builder.Services.AddSingleton(options);
        if (options.Enabled)
        {
            builder.Services.AddOpenTelemetry().WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddMeter("Proofly.Jobs")
                .AddPrometheusExporter());
        }
        return builder;
    }

    /// <summary>Call before MapModules so /metrics bypasses the access pipeline. Off by default.</summary>
    public static WebApplication UseMetricsEndpoint(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<MetricsOptions>();
        if (options.Enabled)
        {
            // Off by default. When on, only allow-listed peers (default loopback) and optionally one local port reach it.
            app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => options.Permits(ctx.Connection.RemoteIpAddress, ctx.Connection.LocalPort));
        }
        return app;
    }

    public static WebApplication MapObservability(this WebApplication app)
    {
        app.MapGet("/readyz", async (IEnumerable<IReadinessCheck> checks, CancellationToken ct) =>
        {
            var results = await Task.WhenAll(checks.Select(c => c.CheckAsync(ct)));
            var ready = results.All(r => r.Ok);
            var body = new
            {
                status = ready ? "ready" : "not_ready",
                checks = results.Select(r => new { name = r.Name, status = r.Ok ? "ok" : "fail", detail = r.Detail }),
            };
            return Results.Json(body, statusCode: ready ? 200 : 503);
        }).AllowAnonymous();

        return app;
    }
}

public sealed record MetricsOptions(bool Enabled, IReadOnlyList<IPAddress> Allowed, int? Port)
{
    public static MetricsOptions From(IConfiguration c)
    {
        var s = c.GetSection("Observability:Metrics");
        var allowed = (s.GetSection("AllowedAddresses").Get<string[]>() ?? ["127.0.0.1", "::1"])
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : throw new InvalidOperationException($"Observability:Metrics:AllowedAddresses contains invalid address '{a}'."))
            .ToList();
        return new(s.GetValue<bool>("Enabled"), allowed, s.GetValue<int?>("Port"));
    }

    public bool Permits(IPAddress? remote, int localPort) =>
        remote is not null
        && Allowed.Any(a => a.Equals(remote) || (remote.IsIPv4MappedToIPv6 && a.Equals(remote.MapToIPv4())))
        && (Port is null || Port == localPort);
}
