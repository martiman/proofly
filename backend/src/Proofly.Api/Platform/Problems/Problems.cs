using System.Text.Json.Serialization;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Json;

namespace Proofly.Api.Platform.Problems;

/// <summary>An RFC 9457 problem type. Slugs are the type names listed in docs/integrations/api/README.md.</summary>
public sealed record ProblemType(string Slug, int Status, string Title)
{
    public const string BaseUri = "https://proofly.example/problems/";
    public string Type => BaseUri + Slug;
}

public static class ProblemTypes
{
    // 401 / 404 / 412 / 428 / 500 are not named in the contract text; their slugs are defined here and should be added to the contract.
    public static readonly ProblemType Unauthenticated = new("unauthenticated", 401, "Authentication required");
    public static readonly ProblemType Forbidden = new("forbidden", 403, "Forbidden");
    public static readonly ProblemType OutOfScope = new("out-of-scope", 403, "Outside the caller's legal-entity scope");
    public static readonly ProblemType SeparationOfDuties = new("separation-of-duties", 403, "Separation of duties violation");
    public static readonly ProblemType NotFound = new("not-found", 404, "Not found");
    public static readonly ProblemType InvalidState = new("invalid-state", 409, "Invalid state");
    public static readonly ProblemType RunSuperseded = new("run-superseded", 409, "Run is superseded");
    public static readonly ProblemType StaleSignoff = new("stale-signoff", 409, "Sign-off is stale");
    public static readonly ProblemType IdempotencyInProgress = new("idempotency-in-progress", 409, "A request with this Idempotency-Key is still in progress");
    public static readonly ProblemType Duplicate = new("duplicate", 409, "Duplicate");
    public static readonly ProblemType PreconditionFailed = new("precondition-failed", 412, "If-Match does not match the current version");
    public static readonly ProblemType Validation = new("validation", 422, "Validation failed");
    public static readonly ProblemType PreconditionRequired = new("precondition-required", 428, "If-Match header is required");
    public static readonly ProblemType Internal = new("internal", 500, "Internal error");
}

/// <summary>Field-level validation error: JSON pointer (body) or parameter name (query/header).</summary>
public sealed record FieldError(string Pointer, string Message, string? Code = null);

/// <summary>Throw from handlers/services to produce a problem+json response. Mapped by the global exception handler.</summary>
public sealed class ProblemException(ProblemType type, string? detail = null, IReadOnlyList<FieldError>? errors = null)
    : Exception(detail ?? type.Title)
{
    public ProblemType ProblemType { get; } = type;
    public string? Detail { get; } = detail;
    public IReadOnlyList<FieldError>? Errors { get; } = errors;

    public static ProblemException Validation(string pointer, string message, string? code = null) =>
        new(ProblemTypes.Validation, message, [new FieldError(pointer, message, code)]);
}

/// <summary>Lets other modules (Data, Domain) translate their own exception types without referencing platform internals.</summary>
public interface IProblemExceptionMapper
{
    ProblemException? Map(Exception exception);
}

public sealed class ProblemBody
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required int Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Detail { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Instance { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RequestId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<FieldError>? Errors { get; init; }
}

public static class ProblemWriter
{
    public const string ContentType = "application/problem+json";

    public static ProblemBody Build(HttpContext ctx, ProblemType type, string? detail, IReadOnlyList<FieldError>? errors) => new()
    {
        Type = type.Type,
        Title = type.Title,
        Status = type.Status,
        Detail = detail,
        Instance = ctx.Request.Path.Value,
        RequestId = ctx.GetRequestId(),
        Errors = errors is { Count: > 0 } ? errors : null,
    };

    public static async Task WriteAsync(HttpContext ctx, ProblemType type, string? detail = null, IReadOnlyList<FieldError>? errors = null)
    {
        var res = ctx.Response;
        // Not Response.Clear(): that would also drop CORS headers the browser needs to read the problem.
        foreach (var h in new[] { "ETag", "Location", "Content-Disposition", "Idempotent-Replayed" }) { res.Headers.Remove(h); }
        res.ContentLength = null;
        res.StatusCode = type.Status;
        res.ContentType = ContentType;
        if (type.Status == 401) { res.Headers.WWWAuthenticate = "Bearer"; }
        if (!res.Headers.ContainsKey(RequestIdMiddleware.HeaderName) && ctx.GetRequestId() is { } id) { res.Headers[RequestIdMiddleware.HeaderName] = id; }
        await System.Text.Json.JsonSerializer.SerializeAsync(res.Body, Build(ctx, type, detail, errors), WireJson.Options, ctx.RequestAborted);
    }
}

/// <summary>Return from a minimal-API handler to emit a problem without throwing.</summary>
public sealed class ProblemResult(ProblemType type, string? detail = null, IReadOnlyList<FieldError>? errors = null) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) => ProblemWriter.WriteAsync(httpContext, type, detail, errors);
}
