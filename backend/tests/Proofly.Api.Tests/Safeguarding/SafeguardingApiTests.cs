using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Safeguarding;

internal sealed class SafeguardingFactory(string schema) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Proofly", $"{SafeguardingApiFixture.Cs};Search Path={schema}");
        builder.UseSetting("Auth:Mode", "Local");
    }

    public HttpClient As(string? token)
    {
        var c = CreateClient();
        if (token is not null) { c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        return c;
    }

    public LocalTokenService Tokens => Services.GetRequiredService<LocalTokenService>();
}

public sealed class SafeguardingApiFixture : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal SafeguardingFactory Factory { get; private set; } = null!;
    internal string ViewerToken => Factory.Tokens.Issue(LocalUsers.Subject(Role.Viewer));
    internal string ApproverToken => Factory.Tokens.Issue(LocalUsers.Subject(Role.Approver));
    internal string NoSafeguardingToken => Factory.Tokens.Issue(LocalUsers.Subject(Role.SecurityAdmin));
    internal string Le1OnlyToken { get; private set; } = null!;
    internal string Le2OnlyToken { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{_schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={_schema}");
        await new MigrationRunner(Db).RunAsync();
        await SafeguardingSeed.ApplyAsync(Db);
        Factory = new SafeguardingFactory(_schema);
        _ = Factory.Server; // seeds the local users
        var dir = new UserDirectory(Db);
        await dir.EnsureUserAsync(new UserRecord("u-le1", "le1", "le1@x.test", "sub-le1", true, [new RoleAssignment("ANALYST", "LE-1")]));
        await dir.EnsureUserAsync(new UserRecord("u-le2", "le2", "le2@x.test", "sub-le2", true, [new RoleAssignment("ANALYST", "LE-2")]));
        Le1OnlyToken = Factory.Tokens.Issue("sub-le1");
        Le2OnlyToken = Factory.Tokens.Issue("sub-le2");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{_schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }
}

public class SafeguardingApiTests(SafeguardingApiFixture f) : IClassFixture<SafeguardingApiFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string[] Strs(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task Position_returns_per_currency_view_matching_the_contract()
    {
        var res = await f.Factory.As(f.ViewerToken).GetAsync("/api/v1/runs/R-1/safeguarding");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // response validation middleware enforces the schema
        var body = await Json(res);
        Assert.Equal("LE-1", body.GetProperty("legalEntityId").GetString());
        Assert.Equal("2026-10-27", body.GetProperty("businessDate").GetString());
        Assert.Equal("APPROVAL_PENDING", body.GetProperty("approvalState").GetString());
        Assert.Equal("QUALIFIED", body.GetProperty("evidenceReadiness").GetString());

        var pos = body.GetProperty("positions");
        Assert.Equal(["EUR", "GBP", "JPY"], pos.EnumerateArray().Select(p => p.GetProperty("currency").GetString()!).ToArray());
        var gbp = pos.EnumerateArray().Single(p => p.GetProperty("currency").GetString() == "GBP");
        Assert.Equal("1000.250000", gbp.GetProperty("obligation").GetProperty("amount").GetString());
        Assert.Equal("-99.750000", gbp.GetProperty("result").GetProperty("amount").GetString());
        Assert.Equal("SHORTFALL", gbp.GetProperty("state").GetString());
        Assert.Equal("-50.000000", gbp.GetProperty("previousResult").GetProperty("amount").GetString());
        Assert.Equal(["EX-1", "EX-2"], Strs(gbp.GetProperty("exceptionIds")));
        Assert.Equal(JsonValueKind.Null, pos.EnumerateArray().Single(p => p.GetProperty("currency").GetString() == "JPY").GetProperty("previousResult").ValueKind);

        Assert.Equal(3, body.GetProperty("obligations").GetArrayLength());
        var lines = body.GetProperty("eligibleBalances").EnumerateArray().ToArray();
        Assert.Equal(5, lines.Length);
        var stale = lines.Single(l => l.GetProperty("sourceRowId").GetString() == "row-2");
        Assert.False(stale.GetProperty("included").GetBoolean());
        Assert.Equal("STALE_BALANCE", stale.GetProperty("reason").GetString());
        Assert.Equal(3, body.GetProperty("adjustments").GetArrayLength());

        var drivers = body.GetProperty("changeAnalysis").EnumerateArray().ToArray();
        Assert.Contains(drivers, d => d.GetProperty("currency").GetString() == "GBP" && d.GetProperty("label").GetString() == "Eligible balances"
            && d.GetProperty("change").GetProperty("amount").GetString() == "-49.500000");
        Assert.DoesNotContain(drivers, d => d.GetProperty("currency").GetString() == "JPY");
    }

    [Fact]
    public async Task Position_of_a_run_without_positions_is_empty_and_incomplete()
    {
        var body = await Json(await f.Factory.As(f.ViewerToken).GetAsync("/api/v1/runs/R-EMPTY/safeguarding"));
        Assert.Equal(0, body.GetProperty("positions").GetArrayLength());
        Assert.Equal("INCOMPLETE", body.GetProperty("evidenceReadiness").GetString());
    }

    [Fact]
    public async Task Adjustments_list_filters_by_status_and_exposes_approval()
    {
        var c = f.Factory.As(f.ApproverToken);
        var all = await Json(await c.GetAsync("/api/v1/runs/R-1/safeguarding/adjustments"));
        Assert.Equal(["ADJ-1", "ADJ-2", "ADJ-3"], all.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray());
        var a1 = all.GetProperty("items")[0];
        Assert.Equal("Ann", a1.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal("2026-11-30", a1.GetProperty("expiresOn").GetString());
        Assert.Equal(["ev-1", "ev-2"], Strs(a1.GetProperty("evidenceRefs")));
        Assert.Equal(2, a1.GetProperty("history").GetArrayLength());
        var pending = all.GetProperty("items")[1].GetProperty("approval");
        Assert.True(pending.GetProperty("canDecide").GetBoolean()); // approver is not the proposer

        var proposed = await Json(await c.GetAsync("/api/v1/runs/R-1/safeguarding/adjustments?status=PROPOSED"));
        Assert.Equal(["ADJ-2"], proposed.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray());
        var none = await Json(await c.GetAsync("/api/v1/runs/R-1/safeguarding/adjustments?status=EXPIRED"));
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());

        var viewerPending = await Json(await f.Factory.As(f.ViewerToken).GetAsync("/api/v1/runs/R-1/safeguarding/adjustments?status=PROPOSED"));
        Assert.False(viewerPending.GetProperty("items")[0].GetProperty("approval").GetProperty("canDecide").GetBoolean());
    }

    [Fact]
    public async Task Adjustments_are_cursor_paged_with_an_optional_total()
    {
        var c = f.Factory.As(f.ApproverToken);
        var first = await Json(await c.GetAsync("/api/v1/runs/R-1/safeguarding/adjustments?limit=2&includeTotal=true"));
        Assert.Equal(["ADJ-1", "ADJ-2"], first.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray());
        Assert.True(first.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, first.GetProperty("page").GetProperty("total").GetInt32());
        var cursor = first.GetProperty("page").GetProperty("nextCursor").GetString();
        var second = await Json(await c.GetAsync("/api/v1/runs/R-1/safeguarding/adjustments?limit=2&cursor=" + Uri.EscapeDataString(cursor!)));
        Assert.Equal(["ADJ-3"], second.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray());
        Assert.False(second.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.False(second.GetProperty("page").TryGetProperty("total", out _)); // omitted, never null
    }

    [Fact]
    public async Task Invalid_status_is_rejected()
    {
        var res = await f.Factory.As(f.ViewerToken).GetAsync("/api/v1/runs/R-1/safeguarding/adjustments?status=NOPE");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/runs/R-1/safeguarding")]
    [InlineData("/api/v1/runs/R-1/safeguarding/adjustments")]
    public async Task Auth_scope_and_permission_are_enforced(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Factory.As(null).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Factory.As(f.NoSafeguardingToken).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Factory.As(f.Le1OnlyToken).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Factory.As(f.Le2OnlyToken).GetAsync(url)).StatusCode); // existence not disclosed
        Assert.Equal(HttpStatusCode.NotFound, (await f.Factory.As(f.ViewerToken).GetAsync(url.Replace("R-1", "R-nope"))).StatusCode);
    }

    [Fact]
    public async Task Other_entity_run_is_visible_only_to_its_scope()
    {
        var own = await Json(await f.Factory.As(f.Le2OnlyToken).GetAsync("/api/v1/runs/R-2/safeguarding"));
        Assert.Equal("LE-2", own.GetProperty("legalEntityId").GetString());
        Assert.Equal(1, own.GetProperty("adjustments").GetArrayLength());
    }
}
