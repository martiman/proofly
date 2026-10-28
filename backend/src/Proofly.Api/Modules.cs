using Proofly.Api.Auth;
using Proofly.Api.Evidence;
using Proofly.Api.Exceptions;
using Proofly.Api.Exceptions.Commands;
using Proofly.Api.Governance;
using Proofly.Api.Governance.Commands;
using Proofly.Api.Health;
using Proofly.Api.Matching;
using Proofly.Api.Matching.Commands;
using Proofly.Api.Platform;
using Proofly.Api.Runs;
using Proofly.Api.Runs.Commands;
using Proofly.Api.Safeguarding;
using Proofly.Api.Safeguarding.Commands;
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
        .AddEvidence(configuration)
        .AddExceptionCommands()
        .AddExceptions()
        .AddGovernance()
        .AddGovernanceCommands()
        .AddHealth()
        .AddMatching()
        .AddMatchingCommands()
        .AddPlatform(configuration)
        .AddResolutionCommands()
        .AddRunCommands()
        .AddRunSignOff()
        .AddRuns()
        .AddSafeguarding()
        .AddSafeguardingCommands()
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
