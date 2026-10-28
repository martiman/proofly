using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Api.Platform;
using Proofly.Api.Platform.Http;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Auth;

internal static class AuthTestContract
{
    public const string Yaml = """
openapi: 3.1.0
info: {title: auth-test, version: 0.0.0}
servers:
  - url: https://{host}/api/v1
    variables: {host: {default: x}}
paths:
  /_auth/entities/{legalEntityId}/runs:
    get:
      operationId: authTestRead
      x-required-permission: run.view
      parameters: [{name: legalEntityId, in: path, required: true, schema: {type: string}}]
      responses: {'200': {description: ok, content: {application/json: {schema: {type: object}}}}}
  /_auth/entities/{legalEntityId}/assign:
    post:
      operationId: authTestCommand
      x-required-permission: exception.assign
      parameters: [{name: legalEntityId, in: path, required: true, schema: {type: string}}]
      responses: {'200': {description: ok, content: {application/json: {schema: {type: object}}}}}
""";
}

internal sealed class AuthTestEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/_auth/entities/{legalEntityId}");
        g.MapGet("/runs", (string legalEntityId, HttpContext ctx) => { ctx.GetCaller().Require(Permission.RunView, legalEntityId); return Results.Ok(new { legalEntityId }); });
        g.MapPost("/assign", (string legalEntityId, HttpContext ctx) => { ctx.GetCaller().Require(Permission.ExceptionAssign, legalEntityId); return Results.Ok(new { legalEntityId }); });
    }
}

internal sealed class AuthFactory(string schema, string mode = "Local", string environment = "Development") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Proofly", $"{AuthFixture.Cs};Search Path={schema}");
        builder.UseSetting("Auth:Mode", mode);
        builder.ConfigureServices(s =>
        {
            s.Configure<PlatformOptions>(o => o.AdditionalContractYaml.Add(AuthTestContract.Yaml));
            s.AddSingleton<IEndpointModule, AuthTestEndpoints>();
        });
    }

    public HttpClient As(string? token)
    {
        var c = CreateClient();
        if (token is not null) { c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        return c;
    }

    public LocalTokenService Tokens => Services.GetRequiredService<LocalTokenService>();
    public string TokenFor(Role role) => Tokens.Issue(LocalUsers.Subject(role));
}

public sealed class AuthFixture : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal AuthFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={Schema}");
        await new MigrationRunner(Db).RunAsync();
        Factory = new AuthFactory(Schema);
        _ = Factory.Server; // starts the host: seeds the local users
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }
}

public class AuthTests(AuthFixture f) : IClassFixture<AuthFixture>
{
    private AuthFactory F => f.Factory;

    private async Task<(string Id, string Token)> NewUser(string key, bool active, params (Role Role, string Le)[] roles)
    {
        var dir = new UserDirectory(f.Db);
        await dir.EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, active,
            roles.Select(r => new RoleAssignment(WireNames.ToUpperSnake(r.Role), r.Le)).ToList()));
        return ("u-" + key, F.Tokens.Issue("sub-" + key));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    public static TheoryData<Role> AllRoles() => new(Enum.GetValues<Role>());

    [Theory, MemberData(nameof(AllRoles))]
    public async Task Me_returns_the_role_bundle_for_each_role(Role role)
    {
        var res = await F.As(F.TokenFor(role)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = await Json(res);
        Assert.Equal([WireNames.ToUpperSnake(role)], me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["*"], me.GetProperty("legalEntityIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(RolePermissions.For(role).Select(WireNames.ToPermission).Order().ToArray(),
            me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()!).Order().ToArray());
        Assert.Equal(LocalUsers.For(role).Email, me.GetProperty("email").GetString());
        // No Environment:* configuration in Development: the host environment name and kind DEV.
        Assert.Equal("Development", me.GetProperty("environment").GetProperty("name").GetString());
        Assert.Equal("DEV", me.GetProperty("environment").GetProperty("kind").GetString());
        Assert.Equal(me.GetProperty("permissions").GetArrayLength(), me.GetProperty("permissionsByLegalEntity").GetProperty("*").GetArrayLength());
    }

    [Fact]
    public async Task Only_approver_and_analyst_style_roles_hold_command_permissions()
    {
        Assert.DoesNotContain(Permission.ExceptionAssign, RolePermissions.For(Role.Viewer));
        Assert.DoesNotContain(Permission.ExceptionApprove, RolePermissions.For(Role.Analyst));
        Assert.Contains(Permission.ExceptionApprove, RolePermissions.For(Role.Approver));
        Assert.DoesNotContain(Permission.SignoffDecide, RolePermissions.For(Role.SecurityAdmin));
        Assert.DoesNotContain(Permission.SignoffDecide, RolePermissions.For(Role.ConfigAdmin));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Viewer_cannot_call_a_command_but_can_read()
    {
        var c = F.As(F.TokenFor(Role.Viewer));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/_auth/entities/LE-1/runs")).StatusCode);
        var denied = await c.PostAsync("/api/v1/_auth/entities/LE-1/assign", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.EndsWith("/forbidden", (await Json(denied)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Analyst_can_call_the_command()
    {
        Assert.Equal(HttpStatusCode.OK, (await F.As(F.TokenFor(Role.Analyst)).PostAsync("/api/v1/_auth/entities/LE-1/assign", null)).StatusCode);
    }

    [Fact]
    public async Task User_without_scope_for_another_legal_entity_cannot_read_it()
    {
        var (_, token) = await NewUser("scoped", true, (Role.Analyst, "LE-1"));
        var c = F.As(token);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/_auth/entities/LE-1/runs")).StatusCode);
        var other = await c.GetAsync("/api/v1/_auth/entities/LE-2/runs");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode); // existence is not disclosed
        var me = await Json(await c.GetAsync("/api/v1/me"));
        Assert.Equal(["LE-1"], me.GetProperty("legalEntityIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Permission_is_per_legal_entity_role()
    {
        var (_, token) = await NewUser("mixed", true, (Role.Analyst, "LE-1"), (Role.Viewer, "LE-2"));
        var c = F.As(token);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/_auth/entities/LE-1/assign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/_auth/entities/LE-2/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/v1/_auth/entities/LE-2/assign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/v1/_auth/entities/LE-3/assign", null)).StatusCode);
    }

    [Fact]
    public async Task Missing_garbage_expired_and_foreign_tokens_are_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(null).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As("not-a-jwt").GetAsync("/api/v1/me")).StatusCode);

        var expired = F.Tokens.Issue(LocalUsers.Subject(Role.Viewer), TimeSpan.FromMinutes(10), issuedAt: DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(expired).GetAsync("/api/v1/me")).StatusCode);

        var foreign = new LocalTokenService(Options.Create(new AuthOptions()), TimeProvider.System).Issue(LocalUsers.Subject(Role.Viewer));
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(foreign).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Session_ends_with_the_token()
    {
        var short1 = F.Tokens.Issue(LocalUsers.Subject(Role.Viewer), TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await F.As(short1).GetAsync("/api/v1/me")).StatusCode);
        var gone = F.Tokens.Issue(LocalUsers.Subject(Role.Viewer), TimeSpan.FromMinutes(5), issuedAt: DateTimeOffset.UtcNow.AddMinutes(-6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(gone).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Unknown_and_inactive_users_are_401_and_not_provisioned_by_default()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(F.Tokens.Issue("sub-unknown", email: "u@x.test")).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(0L, await Count("SELECT count(*) FROM users WHERE external_subject='sub-unknown'"));
        var (_, token) = await NewUser("off", false, (Role.Viewer, "*"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(token).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Unknown_role_ids_grant_nothing()
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-odd", "odd", "odd@x.test", "sub-odd", true, []));
        await using (var cmd = f.Db.CreateCommand("INSERT INTO roles (id,name) VALUES ('LEGACY','x') ON CONFLICT DO NOTHING; INSERT INTO user_roles (user_id,role_id) VALUES ('u-odd','LEGACY')"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        var me = await Json(await F.As(F.Tokens.Issue("sub-odd")).GetAsync("/api/v1/me"));
        Assert.Equal(0, me.GetProperty("permissions").GetArrayLength());
        Assert.Equal(0, me.GetProperty("roles").GetArrayLength());
    }

    [Fact]
    public async Task Local_mode_is_refused_in_production()
    {
        await using var prod = new AuthFactory(f.Schema, "Local", "Production");
        Assert.ThrowsAny<Exception>(() => prod.CreateClient());
    }

    [Fact]
    public async Task Local_mode_is_off_by_default_and_local_tokens_are_not_accepted_in_oidc_mode()
    {
        await using var oidc = new AuthFactory(f.Schema, "Oidc", "Development");
        var token = F.TokenFor(Role.Approver);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oidc.As(token).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(AuthModes.Oidc, new AuthOptions().Mode);
    }

    private async Task<long> Count(string sql)
    {
        await using var c = f.Db.CreateCommand(sql);
        return (long)(await c.ExecuteScalarAsync())!;
    }
}
