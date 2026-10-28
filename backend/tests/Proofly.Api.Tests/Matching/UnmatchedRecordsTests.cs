using System.Net;
using System.Text.Json;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Matching;

/// <summary>Contract batch 3: the paged unmatched population and the capped workbench inline list.</summary>
public class UnmatchedRecordsTests(MatchingFixture f) : IClassFixture<MatchingFixture>
{
    private HttpClient Viewer => f.Factory.As(f.Factory.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string[] Refs(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("transactionId").GetString()!).ToArray();

    [Fact]
    public async Task Unmatched_records_are_paged_with_a_cursor_and_a_total_equal_to_the_stats()
    {
        var all = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/unmatched?includeTotal=true"));
        Assert.Equal(2, all.GetProperty("page").GetProperty("total").GetInt32());
        var wb = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench"));
        Assert.Equal(wb.GetProperty("stats").GetProperty("unmatchedCount").GetInt32(), all.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(Refs(all), wb.GetProperty("unmatched").EnumerateArray().Select(i => i.GetProperty("transactionId").GetString()!).ToArray());

        var first = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/unmatched?limit=1"));
        Assert.Single(Refs(first));
        Assert.True(first.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var cursor = Uri.EscapeDataString(first.GetProperty("page").GetProperty("nextCursor").GetString()!);
        var second = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/unmatched?limit=1&cursor=" + cursor));
        Assert.False(second.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal(Refs(all), Refs(first).Concat(Refs(second)).ToArray());
    }

    [Fact]
    public async Task Unmatched_records_filter_by_pair_currency_and_search()
    {
        string U(string q) => "/api/v1/runs/R-M1/matching/unmatched?includeTotal=true&" + q;
        var eur = await Json(await Viewer.GetAsync(U("currency=EUR")));
        Assert.Equal(1, eur.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(250m, decimal.Parse(eur.GetProperty("items")[0].GetProperty("amount").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, (await Json(await Viewer.GetAsync(U("currency=JPY")))).GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(2, (await Json(await Viewer.GetAsync(U("pair=BANK_TRANSFER")))).GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await Viewer.GetAsync(U("pair=BANK_EVIDENCE")))).GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await Viewer.GetAsync(U("search=REF-5")))).GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await Viewer.GetAsync(U("search=row-2")))).GetProperty("page").GetProperty("total").GetInt32());
        var byId = await Json(await Viewer.GetAsync(U("search=" + Refs(await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/unmatched")))[0])));
        Assert.Equal(1, byId.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await Viewer.GetAsync(U("search=%25")))).GetProperty("page").GetProperty("total").GetInt32()); // wildcards are literal
    }

    [Theory]
    [InlineData("pair=NOPE")]
    [InlineData("currency=eur")]
    [InlineData("cursor=garbage")]
    [InlineData("limit=0")]
    public async Task Invalid_unmatched_filters_are_rejected(string query) =>
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R-M1/matching/unmatched?" + query)).StatusCode);

    [Fact]
    public async Task Unmatched_records_enforce_permission_and_scope()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Factory.As(null).GetAsync("/api/v1/runs/R-M1/matching/unmatched")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Factory.As(f.Factory.TokenFor(Role.ConfigAdmin)).GetAsync("/api/v1/runs/R-M1/matching/unmatched")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/nope/matching/unmatched")).StatusCode);
    }

    [Fact]
    public async Task Workbench_inline_unmatched_list_is_capped_and_the_total_stays_in_stats()
    {
        var capped = await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?unmatchedLimit=1"));
        Assert.Equal(1, capped.GetProperty("unmatched").GetArrayLength());
        Assert.Equal(2, capped.GetProperty("stats").GetProperty("unmatchedCount").GetInt32());
        Assert.Equal(2, (await Json(await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench"))).GetProperty("unmatched").GetArrayLength()); // default 50
        foreach (var bad in new[] { "0", "201", "x" })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R-M1/matching/workbench?unmatchedLimit=" + bad)).StatusCode);
        }
    }
}
