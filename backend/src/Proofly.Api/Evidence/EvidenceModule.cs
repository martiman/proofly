using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Contracts.Governance;
using Proofly.Data.Evidence;
using Proofly.Data.Ingestion;
using Proofly.Domain.Enums;

namespace Proofly.Api.Evidence;

public static class EvidenceModule
{
    public static IServiceCollection AddEvidence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEvidenceData(configuration);
        services.AddSingleton<IEndpointModule, EvidenceEndpoints>();
        return services;
    }
}

internal sealed class EvidenceEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1");

        // generateEvidencePack: 202, the pack is built by the worker (job evidence_pack.generate).
        g.MapPost("/runs/{runId}/evidence-pack", async (string runId, HttpContext ctx, IEvidencePackCommands cmds, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var run = await cmds.GetRunAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.EvidenceExport, run.LegalEntityId);
            var res = await cmds.RequestAsync(runId, caller.UserId, ctx.GetRequestId(), ct);
            return res.Outcome switch
            {
                RequestOutcome.RunNotFound => throw new ProblemException(ProblemTypes.NotFound),
                RequestOutcome.AlreadyGenerating => throw new ProblemException(ProblemTypes.InvalidState, "An evidence pack for this run is already being generated."),
                _ => Results.Json(new EvidencePackDto(res.Pack!.PackId, runId, null, "GENERATING", [], null, null, null), statusCode: StatusCodes.Status202Accepted),
            };
        });

        // downloadEvidencePack: the stored bytes are re-hashed and compared with the recorded SHA-256 before anything is sent.
        g.MapGet("/runs/{runId}/evidence-pack/download", async (string runId, HttpContext ctx, IEvidencePackCommands cmds, EvidenceStore store, CancellationToken ct) =>
        {
            var caller = ctx.GetCaller();
            var run = await cmds.GetRunAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound);
            caller.Require(Permission.EvidenceExport, run.LegalEntityId);
            var pack = await cmds.GetDownloadableAsync(runId, ct) ?? throw new ProblemException(ProblemTypes.NotFound, "No generated evidence pack exists for this run.");

            bool intact;
            try { intact = await store.VerifyAsync(pack.Sha256, ct); }
            catch (IOException) { intact = false; }
            if (!intact)
            {
                await cmds.RecordDownloadAsync(pack, caller.UserId, ctx.GetRequestId(), "INTEGRITY_FAILURE", ct);
                throw new ProblemException(ProblemTypes.Internal, "The stored evidence pack failed its integrity check and was not sent.");
            }
            // Audit first: if the event cannot be written, nothing leaves the server.
            await cmds.RecordDownloadAsync(pack, caller.UserId, ctx.GetRequestId(), "DELIVERED", ct);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"evidence-pack-{pack.RunId}.zip\"";
            ctx.Response.Headers["X-Content-SHA256"] = pack.Sha256;
            return Results.Stream(store.OpenRead(pack.Sha256), "application/zip");
        });
    }
}
