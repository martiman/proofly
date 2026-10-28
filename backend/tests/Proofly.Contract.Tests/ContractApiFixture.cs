using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Platform.Http;
using Proofly.Data;
using Proofly.Domain.Enums;

namespace Proofly.Contract.Tests;

/// <summary>The caller comes from test headers: X-Test-User and a comma separated X-Test-Permissions; scope is every legal entity.</summary>
internal sealed class HeaderCallerAccessor : ICallerAccessor
{
    public Caller? GetCaller(HttpContext ctx)
    {
        var user = ctx.Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) { return null; }
        var perms = ctx.Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        return new Caller(user, perms.ToHashSet(), new HashSet<string> { Caller.AllEntities });
    }
}

/// <summary>The real API in a throw-away schema of proofly_test, migrated and seeded with neutral data.</summary>
public sealed class ContractApiFixture : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    private readonly string _schema = "c_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource _db = null!;
    internal Factory F { get; private set; } = null!;
    /// <summary>The API with the real authentication (Auth:Mode=Local bearer tokens), for operations that read the authenticated user.</summary>
    internal Factory Real { get; private set; } = null!;

    internal static readonly string[] AllPermissions = Enum.GetValues<Permission>().Select(WireNames.ToPermission).ToArray();

    internal sealed class Factory(string schema, bool headerCaller = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Proofly", $"{Cs};Search Path={schema}");
            builder.UseSetting("Auth:Mode", "Local");
            if (headerCaller) { builder.ConfigureServices(s => s.AddSingleton<ICallerAccessor, HeaderCallerAccessor>()); } // last registration wins
        }
    }

    internal HttpClient Client(string? user, IEnumerable<string>? permissions = null)
    {
        var c = F.CreateClient();
        if (user is not null)
        {
            c.DefaultRequestHeaders.Add("X-Test-User", user);
            c.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions ?? []));
        }
        return c;
    }

    internal HttpClient RealViewer()
    {
        var c = Real.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Real.Services.GetRequiredService<Proofly.Api.Auth.LocalTokenService>().Issue(Proofly.Api.Auth.LocalUsers.Subject(Role.Viewer)));
        return c;
    }

    internal HttpClient Everything() => Client("contract-tester", AllPermissions);

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{_schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        _db = NpgsqlDataSource.Create($"{Cs};Search Path={_schema}");
        await new MigrationRunner(_db).RunAsync();
        await using (var c = _db.CreateCommand(Seed.Sql)) { await c.ExecuteNonQueryAsync(); }
        F = new Factory(_schema);
        _ = F.Server;
        Real = new Factory(_schema, headerCaller: false);
        _ = Real.Server;
    }

    public async Task DisposeAsync()
    {
        await Real.DisposeAsync();
        await F.DisposeAsync();
        await _db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{_schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ContractApiCollection : ICollectionFixture<ContractApiFixture>
{
    public const string Name = "contract-api";
}
