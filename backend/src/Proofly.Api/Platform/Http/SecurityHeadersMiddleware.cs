namespace Proofly.Api.Platform.Http;

/// <summary>
/// Response hardening for every API response, errors included: <c>Cache-Control: no-store</c> (financial data must not be kept by
/// browsers or proxies) and <c>X-Content-Type-Options: nosniff</c>. An endpoint that set its own Cache-Control keeps it.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            if (!h.ContainsKey("Cache-Control")) { h["Cache-Control"] = "no-store"; }
            h["X-Content-Type-Options"] = "nosniff";
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}
