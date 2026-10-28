using Proofly.Api.Platform.Problems;

namespace Proofly.Api.Platform.Http;

/// <summary>Global exception handler: domain/platform errors become problem+json; empty 4xx/5xx responses get a problem body.</summary>
public sealed class ProblemExceptionMiddleware(
    RequestDelegate next,
    IEnumerable<IProblemExceptionMapper> mappers,
    ILogger<ProblemExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            if (ctx.Items.ContainsKey(RequestIdMiddleware.InvalidKey))
            {
                throw ProblemException.Validation(RequestIdMiddleware.HeaderName, "X-Request-Id must be a UUID.");
            }
            await next(ctx);
            await FillEmptyErrorAsync(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to write.
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            var problem = Translate(ex);
            if (problem is null)
            {
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
                await ProblemWriter.WriteAsync(ctx, ProblemTypes.Internal);
                return;
            }
            await ProblemWriter.WriteAsync(ctx, problem.ProblemType, problem.Detail, problem.Errors);
        }
    }

    private ProblemException? Translate(Exception ex)
    {
        if (ex is ProblemException p) { return p; }
        foreach (var m in mappers)
        {
            if (m.Map(ex) is { } mapped) { return mapped; }
        }
        // Malformed request bodies that reach model binding are validation failures, not server faults.
        if (ex is BadHttpRequestException b) { return new ProblemException(ProblemTypes.Validation, b.Message); }
        return null;
    }

    private static async Task FillEmptyErrorAsync(HttpContext ctx)
    {
        var res = ctx.Response;
        if (res.HasStarted || res.StatusCode < 400 || res.ContentLength is > 0 || res.ContentType is not null) { return; }
        var type = res.StatusCode switch
        {
            401 => ProblemTypes.Unauthenticated,
            403 => ProblemTypes.Forbidden,
            404 => ProblemTypes.NotFound,
            >= 500 => ProblemTypes.Internal,
            var s => new ProblemType("http-" + s, s, "HTTP " + s),
        };
        await ProblemWriter.WriteAsync(ctx, type);
    }
}
