using Proofly.Data;
using Proofly.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddModules(builder.Configuration);
using var host = builder.Build();

// `Proofly.Worker migrate` applies migrations and exits (no separate CLI project).
if (args.Length > 0 && args[0] == "migrate")
{
    var applied = await host.Services.GetRequiredService<MigrationRunner>()
        .RunAsync();
    Console.WriteLine($"Applied {applied} migration(s).");
    return;
}

await host.RunAsync();
