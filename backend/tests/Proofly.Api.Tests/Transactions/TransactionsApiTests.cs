using System.Net;
using System.Text.Json;
using Proofly.Api.Tests.Auth;
using Proofly.Data.Auth;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Transactions;

public class TransactionsApiTests(AuthFixture f) : IClassFixture<AuthFixture>, IAsyncLifetime
{
    private AuthFactory F => f.Factory;
    private HttpClient Viewer => F.As(F.TokenFor(Role.Viewer));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Seeded = new();

    public Task InitializeAsync() => Seeded.GetOrAdd(f.Schema, _ => SeedAsync());

    private async Task SeedAsync()
    {
        await TxSeed.ApplyAsync(f.Db);
        var dir = new UserDirectory(f.Db);
        await dir.EnsureUserAsync(new UserRecord("u-le-b", "B only", "b@x.test", "sub-le-b", true, [new RoleAssignment("VIEWER", "LE-B")]));
        await dir.EnsureUserAsync(new UserRecord("u-sec", "Sec", "sec@x.test", "sub-sec", true, [new RoleAssignment("SECURITY_ADMIN", "*")]));
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private async Task<JsonElement> Ok(string url, HttpClient? c = null)
    {
        var res = await (c ?? Viewer).GetAsync(url);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await Body(res);
    }

    [Fact]
    public async Task List_returns_contract_page_with_per_currency_aggregates()
    {
        var b = await Ok("/api/v1/runs/R1/transactions?limit=3&includeTotal=true");
        Assert.Equal(3, b.GetProperty("items").GetArrayLength());
        Assert.True(b.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal(10, b.GetProperty("page").GetProperty("total").GetInt32());
        var gbp = b.GetProperty("aggregates").EnumerateArray().Single(a => a.GetProperty("currency").GetString() == "GBP");
        Assert.Equal("307.00", gbp.GetProperty("debitTotal").GetProperty("amount").GetString());
        Assert.Equal("510.50", gbp.GetProperty("creditTotal").GetProperty("amount").GetString());
        Assert.Equal("GBP", gbp.GetProperty("debitTotal").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task List_pages_by_cursor_and_filters()
    {
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var b = await Ok("/api/v1/runs/R1/transactions?limit=4" + (cursor is null ? "" : "&cursor=" + cursor));
            seen.AddRange(b.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            cursor = b.GetProperty("page").GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(10, seen.Distinct().Count());

        var filtered = await Ok("/api/v1/runs/R1/transactions?kind=LEDGER_ENTRY&currency=GBP&hasException=true&search=L-3");
        Assert.Equal(1, filtered.GetProperty("items").GetArrayLength());
        var empty = await Ok("/api/v1/runs/R1/transactions?search=zzz");
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("aggregates").GetArrayLength());
        var window = await Ok("/api/v1/runs/R1/transactions?timestampFrom=2026-10-27T11:30:00Z&timestampTo=2026-10-27T12:30:00Z");
        Assert.Equal(1, window.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_filter_value_is_422()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R1/transactions?kind=NOPE")).StatusCode);
    }

    [Fact]
    public async Task Detail_lineage_and_etag()
    {
        var list = await Ok("/api/v1/runs/R1/transactions?kind=BANK_TRANSFER");
        var id = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("sourceRowId").GetString() == "T-1").GetProperty("id").GetString();
        var res = await Viewer.GetAsync($"/api/v1/runs/R1/transactions/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(res.Headers.ETag);
        var t = await Body(res);
        Assert.Equal("MATCHED", t.GetProperty("matchStatus").GetString());
        Assert.Equal("100.00", t.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("DEBIT", t.GetProperty("direction").GetString()); // transfer instruction = DEBIT of the client account
        Assert.Matches(@"^transfer_instructions:\d+$", t.GetProperty("recordId").GetString());
        Assert.Equal(1, t.GetProperty("deliveryId").GetString() == "D1" ? 1 : 0);

        var lineage = await Ok($"/api/v1/runs/R1/transactions/{id}/lineage");
        Assert.Contains(lineage.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("kind").GetString() == "BANK_TRANSACTION");
        Assert.Equal("D1", lineage.GetProperty("deliveries")[0].GetProperty("deliveryId").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R1/transactions/TRF-0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R1/transactions/TRF-0/lineage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync($"/api/v1/runs/R2/transactions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Lineage_by_subject_for_transaction_record_exception_and_control_result()
    {
        var list = await Ok("/api/v1/runs/R1/transactions?kind=BANK_TRANSFER");
        var tr = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("sourceRowId").GetString() == "T-1");
        var id = tr.GetProperty("id").GetString()!;
        var recordId = tr.GetProperty("recordId").GetString()!;
        var viaTransaction = await Ok($"/api/v1/runs/R1/transactions/{id}/lineage");

        var byKind = await Ok($"/api/v1/lineage/TRANSACTION/{id}");
        Assert.Equal("TRANSACTION", byKind.GetProperty("subjectKind").GetString());
        Assert.Equal(id, byKind.GetProperty("subjectId").GetString());
        Assert.Equal(viaTransaction.GetProperty("nodes").GetRawText(), byKind.GetProperty("nodes").GetRawText());
        Assert.Equal(viaTransaction.GetProperty("edges").GetRawText(), byKind.GetProperty("edges").GetRawText());
        Assert.Equal(byKind.GetRawText(), (await Ok($"/api/v1/lineage/TRANSACTION/{id}?runId=R1")).GetRawText());

        var byRecord = await Ok("/api/v1/lineage/RECORD/" + Uri.EscapeDataString(recordId));
        Assert.Equal("RECORD", byRecord.GetProperty("subjectKind").GetString());
        Assert.Equal(viaTransaction.GetProperty("nodes").GetRawText(), byRecord.GetProperty("nodes").GetRawText());

        var ex = await Ok("/api/v1/lineage/EXCEPTION/X-1");
        Assert.Contains(ex.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("id").GetString() == "EXC-X-1" && n.GetProperty("kind").GetString() == "CONTROL_RESULT");
        Assert.Contains(ex.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("kind").GetString() == "DELIVERY");
        var cr = await Ok("/api/v1/lineage/CONTROL_RESULT/EXC-X-1");
        Assert.Equal("CONTROL_RESULT", cr.GetProperty("subjectKind").GetString());
        Assert.Contains(cr.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("id").GetString() == "EXC-X-1");

        // Unknown subjects, a subject outside the given run and a malformed id are 404; an unknown kind is rejected by the contract (422).
        foreach (var url in new[] { "/api/v1/lineage/TRANSACTION/TRF-0", "/api/v1/lineage/EXCEPTION/X-none", $"/api/v1/lineage/TRANSACTION/{id}?runId=R2", "/api/v1/lineage/TRANSACTION/garbage", "/api/v1/lineage/RECORD/nope:1" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync($"/api/v1/lineage/BOGUS/{id}")).StatusCode);

        // Entity scope (404, no existence leak), permission (403 without evidence.view) and authentication (401).
        var onlyB = F.As(F.Tokens.Issue("sub-le-b"));
        Assert.Equal(HttpStatusCode.NotFound, (await onlyB.GetAsync($"/api/v1/lineage/TRANSACTION/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await onlyB.GetAsync("/api/v1/lineage/EXCEPTION/X-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await F.As(F.Tokens.Issue("sub-sec")).GetAsync("/api/v1/lineage/EXCEPTION/X-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(null).GetAsync("/api/v1/lineage/EXCEPTION/X-1")).StatusCode);
    }

    [Fact]
    public async Task Accounts_list_and_ledger_bridge()
    {
        var accounts = await Ok("/api/v1/runs/R1/accounts?limit=2");
        Assert.Equal(2, accounts.GetProperty("items").GetArrayLength());
        Assert.True(accounts.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var eur = accounts.GetProperty("aggregates").EnumerateArray().Single(a => a.GetProperty("currency").GetString() == "EUR");
        Assert.Equal("-10.00", eur.GetProperty("totalDifference").GetProperty("amount").GetString());
        Assert.Equal(0, (await Ok("/api/v1/runs/R1/accounts?state=PASSED")).GetProperty("items").GetArrayLength());

        var ledger = await Ok("/api/v1/runs/R1/accounts/ACC-2/ledger");
        Assert.Equal("120.00", ledger.GetProperty("account").GetProperty("independentClosingBalance").GetProperty("amount").GetString());
        Assert.Equal("130.00", ledger.GetProperty("account").GetProperty("recordedClosingBalance").GetProperty("amount").GetString());
        Assert.Equal("REVIEW_REQUIRED", ledger.GetProperty("account").GetProperty("state").GetString());
        var bridge = ledger.GetProperty("rollForward").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!, x => x.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("100.00", bridge["Opening balance"]);
        Assert.Equal("20.00", bridge["Credits"]);
        Assert.Equal("120.00", bridge["Independent closing balance"]);
        Assert.Equal("-10.00", bridge["Difference"]); // independent - recorded
        Assert.Single(ledger.GetProperty("entries").EnumerateArray());

        var acc1 = await Ok("/api/v1/runs/R1/accounts/ACC-1/ledger?limit=2");
        Assert.True(acc1.GetProperty("entriesPage").GetProperty("hasMore").GetBoolean());
        var next = await Ok("/api/v1/runs/R1/accounts/ACC-1/ledger?limit=2&cursor=" + acc1.GetProperty("entriesPage").GetProperty("nextCursor").GetString());
        Assert.Equal(1, next.GetProperty("entries").GetArrayLength());
        Assert.Equal("X-1", next.GetProperty("exceptions")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R1/accounts/NOPE/ledger")).StatusCode);
    }

    [Fact]
    public async Task Ledger_of_an_account_in_several_currencies_needs_the_currency()
    {
        await using (var c = f.Db.CreateCommand("""
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
              VALUES ('D5', 'R1', 'LE-A', 'mixed', '2026-10-27', '1', 'e.csv', repeat('e', 64), 'RECEIVED') ON CONFLICT DO NOTHING;
            INSERT INTO account_balances (delivery_id, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency)
            VALUES ('D1', 'ACC-MULTI', 'LE-A', '2026-10-27', 'DAILY_ROLLFORWARD', 0, 1, 'GBP'), ('D5', 'ACC-MULTI', 'LE-A', '2026-10-27', 'DAILY_ROLLFORWARD', 0, 2, 'JPY')
            ON CONFLICT DO NOTHING
            """))
        {
            await c.ExecuteNonQueryAsync();
        }
        var ambiguous = await Viewer.GetAsync("/api/v1/runs/R1/accounts/ACC-MULTI/ledger");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ambiguous.StatusCode);
        Assert.Contains("currency", await ambiguous.Content.ReadAsStringAsync());
        var jpy = await Ok("/api/v1/runs/R1/accounts/ACC-MULTI/ledger?currency=JPY");
        Assert.Equal("JPY", jpy.GetProperty("account").GetProperty("currency").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R1/accounts/ACC-MULTI/ledger?currency=EUR")).StatusCode);
        // a single-currency account needs no currency
        Assert.Equal("GBP", (await Ok("/api/v1/runs/R1/accounts/ACC-1/ledger")).GetProperty("account").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Scope_permission_and_authentication()
    {
        var onlyB = F.As(F.Tokens.Issue("sub-le-b"));
        foreach (var url in new[] { "/api/v1/runs/R1/transactions", "/api/v1/runs/R1/transactions/TRF-1", "/api/v1/runs/R1/transactions/TRF-1/lineage", "/api/v1/runs/R1/accounts", "/api/v1/runs/R1/accounts/ACC-1/ledger" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await onlyB.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await F.As(F.Tokens.Issue("sub-sec")).GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await F.As(null).GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await onlyB.GetAsync("/api/v1/runs/R2/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Viewer.GetAsync("/api/v1/runs/R-NONE/accounts")).StatusCode);
    }
}
