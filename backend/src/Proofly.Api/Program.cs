using Proofly.Api;
using Proofly.Api.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();
builder.Services.AddModules(builder.Configuration);

var app = builder.Build();
app.UseMetricsEndpoint();
app.MapModules();
app.MapObservability();
app.Run();

public partial class Program;
