using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Proofly.Api;
using Proofly.Api.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Benchmarks;

public sealed record EndpointResult(string Name, string Url, int Requests, int Errors, double P50, double P95, double P99, double MaxMs, double Rps, long ResponseBytes);

/// <summary>A real Kestrel host (the production module list, Local auth) on a free loopback port, hit by a few concurrent HttpClients.</summary>
public sealed class ApiBench : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _http;

    private ApiBench(WebApplication app, string baseUrl, string token)
    {
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static async Task<ApiBench> StartAsync(string connectionString, string evidenceDir)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Proofly"] = connectionString, ["Auth:Mode"] = "Local", ["Evidence:Directory"] = evidenceDir,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddModules(builder.Configuration);
        var app = builder.Build();
        app.MapModules();
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var token = app.Services.GetRequiredService<LocalTokenService>().Issue(LocalUsers.Subject(Role.Analyst));
        return new ApiBench(app, url, token);
    }

    public async Task<string> GetAsync(string url)
    {
        using var r = await _http.GetAsync(url);
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{url} -> {(int)r.StatusCode}: {body[..Math.Min(300, body.Length)]}");
        return body;
    }

    /// <summary>Warm-up of 5 requests, then up to <paramref name="maxRequests"/> or <paramref name="maxSeconds"/> over <paramref name="clients"/> concurrent clients.</summary>
    public async Task<EndpointResult> RunAsync(string name, Func<int, string> url, int clients, int maxRequests, double maxSeconds)
    {
        for (var i = 0; i < 5; i++) { try { await GetAsync(url(i)); } catch (Exception e) { Console.WriteLine($"  warm-up {name}: {e.Message}"); break; } }
        var lat = new List<double>();
        var errors = 0; var issued = 0; long bytes = 0;
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, clients).Select(_ => Task.Run(async () =>
        {
            while (wall.Elapsed.TotalSeconds < maxSeconds)
            {
                var i = Interlocked.Increment(ref issued);
                if (i > maxRequests) break;
                var sw = Stopwatch.StartNew();
                try { using var r = await _http.GetAsync(url(i)); var body = await r.Content.ReadAsByteArrayAsync(); Interlocked.Exchange(ref bytes, body.Length); if (!r.IsSuccessStatusCode) Interlocked.Increment(ref errors); }
                catch { Interlocked.Increment(ref errors); }
                sw.Stop();
                lock (lat) lat.Add(sw.Elapsed.TotalMilliseconds);
            }
        })));
        wall.Stop();
        lat.Sort();
        double P(double q) => lat.Count == 0 ? double.NaN : lat[Math.Min(lat.Count - 1, (int)Math.Ceiling(q * lat.Count) - 1)];
        return new EndpointResult(name, url(0), lat.Count, errors, P(0.50), P(0.95), P(0.99), lat.Count == 0 ? double.NaN : lat[^1], lat.Count / wall.Elapsed.TotalSeconds, bytes);
    }

    public async ValueTask DisposeAsync() { _http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); }
}
