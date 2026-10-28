using System.Net;
using System.Text.Json;
using Npgsql;
using Proofly.Api.Tests.Auth;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Matching;

public sealed class MatchingFixture : IAsyncLifetime
{
    private readonly AuthFixture _auth = new();
    internal AuthFactory Factory => _auth.Factory;
    internal NpgsqlDataSource Db => _auth.Db;

    public async Task InitializeAsync()
    {
        await _auth.InitializeAsync();
        await using var c = Db.CreateCommand(MatchingSeed.Sql);
        await c.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _auth.DisposeAsync();
}

public class MatchingApiTests(MatchingFixture f) : IClassFixture<MatchingFixture>
{
    private HttpClient Viewer => f.Factory.As(f.Factory.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> TokenFor(string key, params (Role Role, string Le)[] roles)
    {
        await new UserDirectory(f.Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true,
            roles.Select(r => new RoleAssignment(WireNames.ToUpperSnake(r.Role), r.Le)).ToList()));
        return f.Factory.Tokens.Issue("sub-" + key);
    }

    [Fact]
    public async Task List_returns_contract_shaped_groups()
    {
        var res = await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // response validation middleware checks the schema
        var body = await Json(res);
        var items = body.GetProperty("items");
        Assert.Equal(5, items.GetArrayLength());
        var g3 = items[2];
        Assert.Equal("G-003", g3.GetProperty("id").GetString());
        Assert.Equal("VALUE_MISMATCH", g3.GetProperty("status").GetString());
        Assert.Equal("2.500000", g3.GetProperty("difference").GetProperty("amount").GetString());
        Assert.Equal("JPY", g3.GetProperty("difference").GetProperty("currency").GetString());
        Assert.Equal("rule-ref@2.1", g3.GetProperty("ruleVersion").GetString());
        Assert.Equal("0.01 GBP", g3.GetProperty("tolerance").GetString());
        Assert.Equal("49997.500000", g3.GetProperty("right")[0].GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal(["EX-3"], g3.GetProperty("exceptionIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("difference").ValueKind);
        var g4 = items[3];
        Assert.True(g4.GetProperty("manual").GetBoolean());
        Assert.Equal("AP-1", g4.GetProperty("approval").GetProperty("id").GetString());
        Assert.False(body.GetProperty("page").GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task List_filters_by_status_and_pages_with_cursor()
    {
        var unmatched = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?status=UNMATCHED"));
        Assert.Equal(["G-002", "G-005"], unmatched.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray());

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/v1/runs/R-M1/match-groups?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await Json(await Viewer.GetAsync(url));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
            cursor = page.GetProperty("page").GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null);
        Assert.Equal(3, pages);
        Assert.Equal(["G-001", "G-002", "G-003", "G-004", "G-005"], seen);
    }

    [Fact]
    public async Task List_rejects_bad_cursor_and_status()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?cursor=!!")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?status=BOGUS")).StatusCode);
    }

    [Fact]
    public async Task List_of_run_without_groups_is_empty()
    {
        var body = await Json(await Viewer.GetAsync("/api/v1/runs/R-EMPTY/match-groups"));
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.False(body.GetProperty("page").GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Workbench_defaults_to_first_open_group_with_unmatched_population()
    {
        var res = await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var wb = await Json(res);
        var groups = wb.GetProperty("groups");
        Assert.Equal("G-002", groups[0].GetProperty("id").GetString());
        Assert.StartsWith("transfer_instructions:", wb.GetProperty("focusTransactionId").GetString());
        Assert.Equal(["250.000000", "10.250000"], wb.GetProperty("unmatched").EnumerateArray().Select(u => u.GetProperty("amount").GetProperty("amount").GetString()!).ToArray());
    }

    [Fact]
    public async Task Workbench_focuses_on_transaction_and_exception()
    {
        var list = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/match-groups?status=VALUE_MISMATCH"));
        var tx = list.GetProperty("items")[0].GetProperty("right")[0].GetProperty("transactionId").GetString()!;

        var byTx = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?transactionId=" + Uri.EscapeDataString(tx)));
        Assert.Equal(tx, byTx.GetProperty("focusTransactionId").GetString());
        Assert.Equal("G-003", byTx.GetProperty("groups")[0].GetProperty("id").GetString());

        var byEx = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?exceptionId=EX-3"));
        Assert.Equal(tx, byEx.GetProperty("focusTransactionId").GetString());
    }

    [Fact]
    public async Task Workbench_error_cases()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R-EMPTY/matching/workbench")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?exceptionId=EX-X")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?transactionId=garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?transactionId=card_events:1&exceptionId=EX-1")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/runs/R-M1/match-groups")]
    [InlineData("/api/v1/runs/R-M1/matching/workbench")]
    public async Task Auth_scope_and_permission_rules(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Factory.As(null).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Factory.As(f.Factory.TokenFor(Role.SecurityAdmin)).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync(url.Replace("R-M1", "R-unknown"))).StatusCode);

        var scoped = f.Factory.As(await TokenFor("scoped-" + url.GetHashCode(), (Role.Viewer, "LE-M2")));
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync(url)).StatusCode); // out of scope: existence hidden
        if (url.EndsWith("match-groups", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync("/api/v1/runs/R-M2/match-groups")).StatusCode);
        }
    }

    [Fact]
    public async Task Role_without_matching_view_in_the_entity_is_403()
    {
        var token = await TokenFor("mixed", (Role.Viewer, "LE-M1"), (Role.ConfigAdmin, "LE-M2"));
        var c = f.Factory.As(token);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/runs/R-M1/match-groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/runs/R-M2/match-groups")).StatusCode);
    }
}
