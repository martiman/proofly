namespace Proofly.Api.Platform.Http;

/// <summary>Echoes X-Request-Id (generating a UUID when absent) on every response and puts it in the logging scope.</summary>
public sealed class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    internal const string ItemKey = "proofly.requestId";
    internal const string InvalidKey = "proofly.requestId.invalid";

    public async Task InvokeAsync(HttpContext ctx)
    {
        string id;
        string? invalid = null;
        var supplied = ctx.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(supplied)) { id = Guid.NewGuid().ToString(); }
        else if (Guid.TryParse(supplied, out var g)) { id = g.ToString(); }
        else { id = Guid.NewGuid().ToString(); invalid = supplied; }

        ctx.Items[ItemKey] = id;
        ctx.Response.OnStarting(() => { ctx.Response.Headers[HeaderName] = id; return Task.CompletedTask; });
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id }))
        {
            // Invalid input is rejected, not replaced (see ProblemExceptionMiddleware); the response carries the server-generated id.
            if (invalid is not null) { ctx.Items[InvalidKey] = true; }
            await next(ctx);
        }
    }
}

public static class RequestIdExtensions
{
    /// <summary>The correlation id of the current request. Store it on audit events (AuditEvent.requestId).</summary>
    public static string? GetRequestId(this HttpContext ctx) => ctx.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var v) ? v as string : null;
}
