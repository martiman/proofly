using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Proofly.Api.Platform.Contract;
using Proofly.Api.Platform.Idempotency;
using Proofly.Api.Platform.Problems;
using Proofly.Domain.Enums;

namespace Proofly.Api.Platform.Http;

internal sealed class ContractFeature
{
    public const string ItemKey = "proofly.contract";
    public OperationSpec? Operation { get; set; }
    public JsonNode? Body { get; set; }
    public string? CanonicalBody { get; set; }
}

internal static class ContractContext
{
    public static ContractFeature Feature(this HttpContext ctx)
    {
        if (!ctx.Items.TryGetValue(ContractFeature.ItemKey, out var f)) { f = ctx.Items[ContractFeature.ItemKey] = new ContractFeature(); }
        return (ContractFeature)f!;
    }

    public static async Task<byte[]> CaptureAsync(HttpContext ctx, RequestDelegate next)
    {
        var original = ctx.Response.Body;
        var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await next(ctx); }
        finally { ctx.Response.Body = original; }
        return buffer.ToArray();
    }

    public static async Task FlushAsync(HttpContext ctx, byte[] body)
    {
        if (body.Length == 0) { return; }
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
    }
}

/// <summary>Stage 1: match the contract operation, authenticate (401), authorise by x-required-permission (403), require If-Match (428).</summary>
public sealed class AccessMiddleware(RequestDelegate next, OpenApiContract contract, ICallerAccessor accessor)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var endpoint = ctx.GetEndpoint();
        if (endpoint is null) { await next(ctx); return; } // unknown route: 404/405 handled downstream

        var op = contract.Match(ctx.Request.Method, ctx.Request.Path);
        ctx.Feature().Operation = op;

        if (op is null)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null) { await next(ctx); return; }
            var c = accessor.GetCaller(ctx) ?? throw new ProblemException(ProblemTypes.Unauthenticated);
            ctx.Items[CallerExtensions.ItemKey] = c;
            if (endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>() is { } req && !c.Has(req.Permission))
            {
                throw new ProblemException(ProblemTypes.Forbidden, "The caller lacks the required permission.");
            }
            await next(ctx);
            return;
        }

        var caller = accessor.GetCaller(ctx) ?? throw new ProblemException(ProblemTypes.Unauthenticated);
        ctx.Items[CallerExtensions.ItemKey] = caller;
        var needed = op.RequiredPermission;
        if (needed is not null && needed != WireNames.ToPermission(Permission.AuthSelf) && !caller.Permissions.Contains(needed))
        {
            throw new ProblemException(ProblemTypes.Forbidden, "The caller lacks the required permission.");
        }
        if (op.RequiresIfMatch && string.IsNullOrWhiteSpace(ctx.Request.Headers.IfMatch.ToString()))
        {
            throw new ProblemException(ProblemTypes.PreconditionRequired, "Send the ETag of the resource as last read in If-Match.");
        }
        await next(ctx);
    }
}

/// <summary>Stage 2: validate query, headers and JSON body against the contract. Rejects with 422 and field errors; never repairs.</summary>
public sealed class RequestValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var feature = ctx.Feature();
        if (feature.Operation is not { } op) { await next(ctx); return; }
        var errors = new List<FieldError>();

        ValidateQuery(ctx, op, errors);
        ValidateHeaders(ctx, op, errors);
        await ValidateBodyAsync(ctx, op, feature, errors);

        if (errors.Count > 0) { throw new ProblemException(ProblemTypes.Validation, "The request does not match the API contract.", errors); }
        await next(ctx);
    }

    private static void ValidateQuery(HttpContext ctx, OperationSpec op, List<FieldError> errors)
    {
        var declared = op.Parameters.Where(p => p.In == "query").ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var key in ctx.Request.Query.Keys.Where(k => !declared.ContainsKey(k)))
        {
            errors.Add(new FieldError(key, "Unknown query parameter.", "unknown-parameter"));
        }
        foreach (var p in declared.Values)
        {
            if (!ctx.Request.Query.TryGetValue(p.Name, out var values))
            {
                if (p.Required) { errors.Add(new FieldError(p.Name, "Required query parameter is missing.", "required")); }
                continue;
            }
            ValidateParam(op, p, values.ToArray()!, errors);
        }
    }

    private static void ValidateHeaders(HttpContext ctx, OperationSpec op, List<FieldError> errors)
    {
        foreach (var p in op.Parameters.Where(p => p.In == "header"))
        {
            var present = ctx.Request.Headers.TryGetValue(p.Name, out var values) && values.Count > 0;
            if (!present)
            {
                // A missing If-Match is 428, raised by AccessMiddleware.
                if (p.Required && !p.Name.Equals("If-Match", StringComparison.OrdinalIgnoreCase)) { errors.Add(new FieldError(p.Name, "Required header is missing.", "required")); }
                continue;
            }
            ValidateParam(op, p, values.ToArray()!, errors);
        }
    }

    private static void ValidateParam(OperationSpec op, ParameterSpec p, string[] raw, List<FieldError> errors)
    {
        JsonNode? instance;
        if (p.Type == "array")
        {
            var parts = p.CommaSeparated ? raw.SelectMany(r => r.Split(',')) : raw;
            instance = new JsonArray(parts.Select(x => Typed(x, p.ItemType)).ToArray());
        }
        else
        {
            if (raw.Length > 1) { errors.Add(new FieldError(p.Name, "The parameter must be given once.", "duplicate")); return; }
            instance = Typed(raw[0], p.Type);
        }
        foreach (var (_, message) in op.Document.Validate(p.SchemaPointer, instance))
        {
            errors.Add(new FieldError(p.Name, message, "invalid"));
        }
    }

    private static JsonNode? Typed(string s, string? type) => type switch
    {
        "integer" when long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
        "number" when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        "boolean" when s is "true" or "false" => JsonValue.Create(s == "true"),
        _ => JsonValue.Create(s),
    };

    private static async Task ValidateBodyAsync(HttpContext ctx, OperationSpec op, ContractFeature feature, List<FieldError> errors)
    {
        if (!op.BodyDeclared) { return; }
        ctx.Request.EnableBuffering();
        string text;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(ctx.RequestAborted);
        }
        ctx.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            if (op.BodyRequired) { errors.Add(new FieldError("", "A JSON request body is required.", "required")); }
            return;
        }
        if (op.BodySchemaPointer is null) { return; }
        if (ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            errors.Add(new FieldError("", "Content-Type must be application/json.", "content-type"));
            return;
        }
        try
        {
            feature.Body = JsonNode.Parse(text);
            feature.CanonicalBody = Canonical.Serialize(feature.Body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            errors.Add(new FieldError("", "The request body is not valid JSON.", "malformed"));
            return;
        }
        foreach (var (pointer, message) in op.Document.Validate(op.BodySchemaPointer, feature.Body))
        {
            errors.Add(new FieldError(pointer, message, "invalid"));
        }
    }
}

internal static class Canonical
{
    /// <summary>Key-order- and whitespace-independent JSON text, used to compare idempotent request bodies.</summary>
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? n, StringBuilder sb)
    {
        switch (n)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    Write(v, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) { sb.Append(','); } Write(a[i], sb); }
                sb.Append(']');
                break;
            default: sb.Append(n.ToJsonString()); break;
        }
    }
}

/// <summary>Stage 3: Idempotency-Key replay for commands. Only 2xx responses are stored; other outcomes release the key.</summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, IIdempotencyStore store, IOptions<PlatformOptions> options)
{
    private static readonly string[] ReplayedHeaders = ["ETag", "Location"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        var feature = ctx.Feature();
        if (feature.Operation is not { RequiresIdempotencyKey: true } op) { await next(ctx); return; }

        var caller = ctx.GetCaller();
        var key = ctx.Request.Headers["Idempotency-Key"].ToString().ToLowerInvariant();
        var scope = caller.UserId;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString}\n{feature.CanonicalBody}")));

        var begin = await store.BeginAsync(scope, key, hash, options.Value.IdempotencyTtl, ctx.RequestAborted);
        switch (begin.Outcome)
        {
            case IdempotencyOutcome.Replay:
                await ReplayAsync(ctx, begin.Response!);
                return;
            case IdempotencyOutcome.Mismatch:
                throw ProblemException.Validation("Idempotency-Key", "This Idempotency-Key was already used with a different request.", "idempotency-key-reuse");
            case IdempotencyOutcome.InProgress:
                throw new ProblemException(ProblemTypes.IdempotencyInProgress, "A request with this Idempotency-Key is still being processed.");
        }

        var completed = false;
        try
        {
            var body = await ContractContext.CaptureAsync(ctx, next);
            var status = ctx.Response.StatusCode;
            if (status is >= 200 and < 300)
            {
                var headers = ReplayedHeaders
                    .Where(h => ctx.Response.Headers.ContainsKey(h))
                    .ToDictionary(h => h, h => ctx.Response.Headers[h].ToString());
                await store.CompleteAsync(scope, key, new StoredResponse(status, ctx.Response.ContentType, headers, body, ctx.GetRequestId()), CancellationToken.None);
                completed = true;
            }
            await ContractContext.FlushAsync(ctx, body);
        }
        finally
        {
            if (!completed) { await store.ReleaseAsync(scope, key, CancellationToken.None); }
        }
    }

    private static async Task ReplayAsync(HttpContext ctx, StoredResponse r)
    {
        ctx.Response.StatusCode = r.StatusCode;
        if (r.ContentType is not null) { ctx.Response.ContentType = r.ContentType; }
        foreach (var (k, v) in r.Headers) { ctx.Response.Headers[k] = v; }
        ctx.Response.Headers["Idempotent-Replayed"] = "true";
        await ContractContext.FlushAsync(ctx, r.Body);
    }
}

/// <summary>Stage 4: the response must match the contract (documented status, schema). A violation is a server bug: logged, 500.</summary>
public sealed class ResponseValidationMiddleware(RequestDelegate next, IOptions<PlatformOptions> options, ILogger<ResponseValidationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!options.Value.ValidateResponses || ctx.Feature().Operation is not { } op) { await next(ctx); return; }

        var body = await ContractContext.CaptureAsync(ctx, next);
        var status = ctx.Response.StatusCode;
        // Error bodies are produced by the platform problem writer; 304 is a conditional-GET result.
        if (status >= 400 || status == 304) { await ContractContext.FlushAsync(ctx, body); return; }

        var violation = Check(op, status, ctx.Response.ContentType, body);
        if (violation is not null)
        {
            logger.LogError("Response contract violation for {Operation} ({Status}): {Violation}", op.OperationId, status, violation);
            throw new ProblemException(ProblemTypes.Internal);
        }
        await ContractContext.FlushAsync(ctx, body);
    }

    private static string? Check(OperationSpec op, int status, string? contentType, byte[] body)
    {
        if (!op.Responses.TryGetValue(status.ToString(), out var spec) && !op.Responses.TryGetValue("default", out spec))
        {
            return "status is not documented";
        }
        if (!spec.HasBody) { return body.Length == 0 ? null : "response must have no body"; }
        if (spec.SchemaPointer is null) { return null; }
        if (contentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) { return $"content type {contentType} is not application/json"; }
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { return "body is not valid JSON"; }
        var errors = op.Document.Validate(spec.SchemaPointer, node);
        return errors.Count == 0 ? null : string.Join("; ", errors.Take(5).Select(e => $"{e.Pointer}: {e.Message}"));
    }
}
