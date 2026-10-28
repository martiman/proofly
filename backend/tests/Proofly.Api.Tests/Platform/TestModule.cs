using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Api.Platform.Problems;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Platform;

/// <summary>Test-only OpenAPI document: every platform convention is exercised through these operations.</summary>
internal static class TestContract
{
    public const string Yaml = """
openapi: 3.1.0
info: {title: test, version: 0.0.0}
servers:
  - url: https://{host}/api/v1
    variables: {host: {default: x}}
paths:
  /_test/problem/{slug}:
    get:
      operationId: testProblem
      x-required-permission: auth.self
      parameters: [{name: slug, in: path, required: true, schema: {type: string}}]
      responses: {'200': {description: ok}}
  /_test/boom:
    get:
      operationId: testBoom
      x-required-permission: auth.self
      responses: {'200': {description: ok}}
  /_test/secure:
    get:
      operationId: testSecure
      x-required-permission: run.view
      responses:
        '200': {description: ok, content: {application/json: {schema: {type: object}}}}
  /_test/items:
    get:
      operationId: testItems
      x-required-permission: auth.self
      parameters:
        - {name: limit, in: query, schema: {type: integer, minimum: 1, maximum: 200, default: 25}}
        - {name: cursor, in: query, schema: {type: string}}
        - {name: includeTotal, in: query, schema: {type: boolean, default: false}}
        - {name: severity, in: query, style: form, explode: false, schema: {type: array, items: {$ref: '#/components/schemas/Severity'}}}
      responses:
        '200': {description: ok, content: {application/json: {schema: {$ref: '#/components/schemas/ItemPage'}}}}
  /_test/things/{id}:
    get:
      operationId: testGetThing
      x-required-permission: auth.self
      parameters: [{name: id, in: path, required: true, schema: {type: string}}]
      responses:
        '200': {description: ok, content: {application/json: {schema: {$ref: '#/components/schemas/Thing'}}}}
  /_test/things/{id}/rename:
    post:
      operationId: testRenameThing
      x-required-permission: auth.self
      parameters:
        - {name: id, in: path, required: true, schema: {type: string}}
        - {name: If-Match, in: header, required: true, schema: {type: string}}
        - {name: Idempotency-Key, in: header, required: true, schema: {type: string, format: uuid}}
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [name, reason]
              additionalProperties: false
              properties:
                name: {type: string, minLength: 1}
                reason: {type: string, minLength: 3, maxLength: 4000}
      responses:
        '200': {description: ok, content: {application/json: {schema: {$ref: '#/components/schemas/Thing'}}}}
  /_test/slow:
    post:
      operationId: testSlow
      x-required-permission: auth.self
      parameters: [{name: Idempotency-Key, in: header, required: true, schema: {type: string, format: uuid}}]
      requestBody: {required: true, content: {application/json: {schema: {type: object}}}}
      responses: {'202': {description: accepted, content: {application/json: {schema: {type: object}}}}}
  /_test/wire:
    get:
      operationId: testWire
      x-required-permission: auth.self
      responses:
        '200': {description: ok, content: {application/json: {schema: {$ref: '#/components/schemas/Wire'}}}}
  /_test/bad-response:
    get:
      operationId: testBadResponse
      x-required-permission: auth.self
      responses:
        '200': {description: ok, content: {application/json: {schema: {$ref: '#/components/schemas/Thing'}}}}
components:
  schemas:
    Severity: {type: string, enum: [CRITICAL, HIGH, MEDIUM, LOW]}
    Thing:
      type: object
      required: [id, name, version]
      properties: {id: {type: string}, name: {type: string}, version: {type: integer}}
    ItemPage:
      type: object
      required: [items, page]
      properties:
        items: {type: array, items: {type: integer}}
        page:
          type: object
          required: [hasMore]
          properties: {nextCursor: {type: [string, 'null']}, hasMore: {type: boolean}, total: {type: integer}}
    Wire:
      type: object
      required: [amount, severity, area, permission]
      properties:
        amount: {type: string, pattern: '^-?\d+(\.\d{1,6})?$'}
        severity: {type: string, enum: [HIGH]}
        area: {type: string, enum: [independent_ledger]}
        permission: {type: string, enum: [audit.view]}
""";
}

internal sealed record CursorState(int After);

internal sealed class TestCallerAccessor : ICallerAccessor
{
    public Caller? GetCaller(HttpContext ctx)
    {
        var user = ctx.Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) { return null; }
        var perms = ctx.Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        return new Caller(user, perms.ToHashSet(), new HashSet<string> { "LE-1" });
    }
}

/// <summary>Handlers for the test contract. State is per factory instance (one per test class).</summary>
internal sealed class TestEndpoints : IEndpointModule
{
    public int RenameCalls;
    public long ThingVersion = 1;
    public string ThingName = "alpha";
    public readonly TaskCompletionSource SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource SlowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/_test");

        g.MapGet("/problem/{slug}", (string slug) =>
        {
            var t = typeof(ProblemTypes).GetFields().Select(f => (ProblemType)f.GetValue(null)!).First(p => p.Slug == slug);
            throw new ProblemException(t, "test detail", t.Slug == "validation" ? [new FieldError("/x", "bad", "code")] : null);
        });
        g.MapGet("/boom", () => { throw new InvalidOperationException("secret internals"); });
        g.MapGet("/secure", () => Results.Ok(new { ok = true }));

        g.MapGet("/items", (HttpContext ctx) =>
        {
            var req = PageRequest.From(ctx);
            var after = req.Decode<CursorState>()?.After ?? 0;
            var fetched = Enumerable.Range(1, 60).Where(i => i > after).Take(req.Limit + 1).ToList();
            return Results.Ok(Paging.Create(fetched, req, last => new CursorState(last), total: 60));
        });

        g.MapGet("/things/{id}", (string id, HttpContext ctx) =>
        {
            var etag = ETags.ForVersion(ThingVersion);
            if (ctx.IsNotModified(etag)) { ctx.Set(etag); return Results.StatusCode(304); }
            ctx.Set(etag);
            return Results.Ok(new { id, name = ThingName, version = ThingVersion });
        });

        g.MapPost("/things/{id}/rename", (string id, RenameBody body, HttpContext ctx) =>
        {
            ctx.RequireIfMatch(ETags.ForVersion(ThingVersion));
            Interlocked.Increment(ref RenameCalls);
            ThingName = body.Name;
            ThingVersion++;
            ctx.Set(ETags.ForVersion(ThingVersion));
            return Results.Ok(new { id, name = ThingName, version = ThingVersion });
        });

        g.MapPost("/slow", async () =>
        {
            SlowStarted.TrySetResult();
            await SlowRelease.Task;
            return Results.Json(new { done = true }, statusCode: 202);
        });

        g.MapGet("/wire", () => Results.Ok(new WireOut(-50.10m, Severity.High, ControlArea.IndependentLedger, Permission.AuditView)));
        g.MapGet("/bad-response", () => Results.Ok(new { id = "x", version = "not-an-int" }));

        // Not in the contract: must be authenticated unless AllowAnonymous.
        g.MapGet("/unlisted", () => "x");
        g.MapGet("/open", () => "x").AllowAnonymous();
        g.MapGet("/audit-only", () => "x").RequirePermission(Permission.AuditView);
    }

    internal sealed record RenameBody(string Name, string Reason);
    internal sealed record WireOut(decimal Amount, Severity Severity, ControlArea Area, Permission Permission);
}

internal sealed class PlatformFactory : WebApplicationFactory<Program>
{
    public TestEndpoints Endpoints { get; } = new();
    public bool ValidateResponses { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Platform:IdempotencyStore", "InMemory"); // the default schema of proofly_test is not migrated
        builder.UseSetting("ConnectionStrings:Proofly", "Host=localhost;Port=55432;Username=proofly;Database=proofly_test");
        builder.ConfigureServices(s =>
        {
            s.Configure<PlatformOptions>(o => { o.AdditionalContractYaml.Add(TestContract.Yaml); o.ValidateResponses = ValidateResponses; });
            s.AddSingleton<ICallerAccessor, TestCallerAccessor>();
            s.AddSingleton<IEndpointModule>(Endpoints);
        });
    }

    public HttpClient Client(string? user = "alice", string permissions = "") 
    {
        var c = CreateClient();
        if (user is not null) { c.DefaultRequestHeaders.Add("X-Test-User", user); }
        c.DefaultRequestHeaders.Add("X-Test-Permissions", permissions);
        return c;
    }
}
