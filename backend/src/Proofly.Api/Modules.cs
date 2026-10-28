using Proofly.Api.Auth;
using Proofly.Api.Exceptions;
using Proofly.Api.Governance;
using Proofly.Api.Health;
using Proofly.Api.Matching;
using Proofly.Api.Platform;
using Proofly.Api.Runs;
using Proofly.Api.Safeguarding;
using Proofly.Api.Sources;
using Proofly.Api.Transactions;
using Proofly.Data;
using Proofly.Data.Audit;

namespace Proofly.Api;

/// <summary>One line per module, alphabetical. Parallel agents add a line here and nowhere else in Program.cs.</summary>
public static class Modules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddAuth(configuration)
        .AddData(configuration)
        .AddAudit()
        .AddExceptions()
        .AddGovernance()
        .AddHealth()
        .AddMatching()
        .AddPlatform(configuration)
        .AddRuns()
        .AddSafeguarding()
        .AddSources()
        .AddTransactions();

    public static WebApplication MapModules(this WebApplication app)
    {
        app.UsePlatform(); // must stay first: it installs the middleware pipeline
        app.MapAuth();
        app.MapHealth();
        app.MapPlatformEndpoints();
        return app;
    }
}
