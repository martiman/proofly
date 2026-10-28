using System.Net;
using System.Text.Json;
using Proofly.Api.Tests.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Runs;

/// <summary>
/// Regression tests for unbounded response bodies found by the 100,000-transaction spike: the sign-off pack, the safeguarding position and the control
/// room must stay small however many exceptions or lines a run has, and the full lists must be reachable through the paged operations.
/// </summary>
public sealed class ResponseSizeTests(AuthFixture f) : IClassFixture<AuthFixture>, IAsyncLifetime
{
    private const int Exceptions = 6000;
    private const int Lines = 3000;
    private const string Run = "RUN-SZ1";
    private AuthFactory F => f.Factory;
    private HttpClient Approver => F.As(F.TokenFor(Role.Approver));

    public async Task InitializeAsync()
    {
        await using var chk = f.Db.CreateCommand("SELECT count(*) FROM runs WHERE id=@r");
        chk.Parameters.AddWithValue("r", Run);
        if ((long)(await chk.ExecuteScalarAsync())! > 0) { return; }
        await Exec($$"""
            INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('LE-SZ','Entity SZ','GBP','{EUR}') ON CONFLICT DO NOTHING;
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state, sign_off_state, result_hash)
              VALUES ('{{Run}}','LE-SZ','2020-02-02',1,'rs1','REVIEW_REQUIRED','PREPARATION_INCOMPLETE', repeat('a',64));
            INSERT INTO run_control_results (run_id, control, state, rule_version, record_count, finding_count, blocking_finding_count)
              VALUES ('{{Run}}','data_completeness','PASSED','c1',10,0,0), ('{{Run}}','matching','REVIEW_REQUIRED','c1',10,{{Exceptions}},10);
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
              VALUES ('DLV-SZ1','{{Run}}','LE-SZ','bank_transactions','2020-02-02','1','sz.csv',repeat('c',64),'RECEIVED'),
                     ('DLV-SZ2','{{Run}}','LE-SZ','safeguarding_balances','2020-02-02','1','sz2.csv',repeat('d',64),'RECEIVED');
            -- the first 10 exceptions are blocking, the 20 after them CRITICAL; every exception carries a value that grows with its number
            INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking, delivery_id, source_row_id)
              SELECT 'EXSZ-' || lpad(n::text, 6, '0'), '{{Run}}', 'LE-SZ', '2020-02-02', 'Unmatched bank row ' || n, 'matching', 'NO_CANDIDATE',
                     CASE WHEN n BETWEEN 11 AND 30 THEN 'CRITICAL' WHEN n % 3 = 0 THEN 'HIGH' ELSE 'MEDIUM' END,
                     CASE WHEN n % 7 = 0 THEN 'RESOLVED' ELSE 'OPEN' END, n::numeric / 100, 'GBP', '2020-02-05T00:00Z', n <= 10, 'DLV-SZ1', 'row-' || n
              FROM generate_series(1, {{Exceptions}}) AS n;
            INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at, blocking)
              SELECT 'EXSZ-SG' || n, '{{Run}}', 'LE-SZ', '2020-02-02', 'Safeguarding ' || n, 'safeguarding', 'SHORTFALL', 'HIGH', 'OPEN', n, 'GBP', '2020-02-05T00:00Z', false
              FROM generate_series(1, 40) AS n;
            INSERT INTO safeguarding_balances (delivery_id, source_row_id, external_account_id, legal_entity_id, balance_timestamp, balance_type, balance_amount, currency, evidence_reference)
              SELECT 'DLV-SZ2', 'sg-' || n, 'EXT-' || lpad(n::text, 5, '0'), 'LE-SZ', '2020-02-02T20:00:00Z', 'CLOSING', n * 10 + 0.5, 'GBP', 'stmt-' || n FROM generate_series(1, {{Lines}}) AS n;
            INSERT INTO safeguarding_positions (run_id, legal_entity_id, currency, obligation_amount, eligible_amount, difference_amount, state, detail)
              SELECT '{{Run}}', 'LE-SZ', 'GBP', 1000, 900, -100, 'SHORTFALL', jsonb_build_object(
                'approvedAdjustments', '0', 'qualifications', '[]'::jsonb,
                'obligationLines', (SELECT jsonb_agg(jsonb_build_object('accountId', 'CA-' || lpad(n::text, 5, '0'), 'amount', (n * 3 % 1000 + 1)::text || '.25', 'movementType', 'CLIENT_FUNDS')) FROM generate_series(1, {{Lines}}) AS n),
                'balanceLines', (SELECT jsonb_agg(jsonb_build_object('sourceRowId', 'sg-' || n, 'included', true, 'reason', 'ELIGIBLE', 'findingIds', '[]'::jsonb)) FROM generate_series(1, {{Lines}}) AS n));
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Exec(string sql) { await using var c = f.Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }

    private static async Task<(JsonElement Body, int Bytes)> Get(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)res.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");
        return (JsonDocument.Parse(bytes).RootElement, bytes.Length);
    }

    [Fact]
    public async Task Sign_off_pack_lists_the_top_open_matters_and_counts_the_rest()
    {
        var (b, size) = await Get(Approver, $"/api/v1/runs/{Run}/sign-off");
        var open = Exceptions - Exceptions / 7 + 40; // 6000 minus the resolved ones (multiples of 7), plus the 40 safeguarding exceptions
        Assert.Equal(50, b.GetProperty("openMatters").GetArrayLength());
        Assert.Equal(open, b.GetProperty("openMattersTotal").GetInt32());
        Assert.True(size < 100_000, $"sign-off response is {size} bytes");

        var ids = b.GetProperty("openMatters").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
        // blocking first (n 1..10, minus the resolved n = 7), then the CRITICAL ones (n 11..30, minus 14, 21 and 28)
        Assert.All(b.GetProperty("openMatters").EnumerateArray().Take(9), e => Assert.True(e.GetProperty("blocking").GetBoolean()));
        Assert.Equal("EXSZ-000009", ids[0]); // blocking, then HIGH (multiples of 3), then the largest value
        Assert.Equal("CRITICAL", b.GetProperty("openMatters")[9].GetProperty("severity").GetString());

        var byCategory = b.GetProperty("openMattersByCategory").EnumerateArray().ToDictionary(e => e.GetProperty("control").GetString()!);
        Assert.Equal(40, byCategory["safeguarding"].GetProperty("total").GetInt32());
        Assert.Equal(open - 40, byCategory["matching"].GetProperty("total").GetInt32());
        Assert.Equal(9, byCategory["matching"].GetProperty("blocking").GetInt32());

        var matching = b.GetProperty("checklist").EnumerateArray().Single(c => c.GetProperty("control").GetString() == "matching");
        Assert.Equal(50, matching.GetProperty("exceptionIds").GetArrayLength());
        Assert.Equal(open - 40, matching.GetProperty("exceptionCount").GetInt32());
    }

    [Fact]
    public async Task Sign_off_limit_is_validated_and_honoured()
    {
        var (b, _) = await Get(Approver, $"/api/v1/runs/{Run}/sign-off?openMattersLimit=5");
        Assert.Equal(5, b.GetProperty("openMatters").GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Approver.GetAsync($"/api/v1/runs/{Run}/sign-off?openMattersLimit=201")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Approver.GetAsync($"/api/v1/runs/{Run}/sign-off?openMattersLimit=0")).StatusCode);
    }

    [Fact]
    public async Task Open_matters_pages_through_the_whole_set_in_the_pack_order_without_gaps_or_repeats()
    {
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (b, size) = await Get(Approver, $"/api/v1/runs/{Run}/sign-off/open-matters?limit=200&includeTotal=true" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.True(size < 200_000, $"open-matters page is {size} bytes");
            Assert.Equal(Exceptions - Exceptions / 7 + 40, b.GetProperty("page").GetProperty("total").GetInt32());
            seen.AddRange(b.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
            cursor = b.GetProperty("page").TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            pages++;
        } while (cursor is not null && pages < 100);

        Assert.Equal(Exceptions - Exceptions / 7 + 40, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        var (pack, _) = await Get(Approver, $"/api/v1/runs/{Run}/sign-off?openMattersLimit=200");
        Assert.Equal(pack.GetProperty("openMatters").EnumerateArray().Select(e => e.GetProperty("id").GetString()), seen.Take(200));
    }

    [Fact]
    public async Task Open_matters_filters_by_blocking_and_severity_and_rejects_bad_values()
    {
        var (blocking, _) = await Get(Approver, $"/api/v1/runs/{Run}/sign-off/open-matters?blocking=true&includeTotal=true");
        Assert.Equal(9, blocking.GetProperty("page").GetProperty("total").GetInt32());
        var (critical, _) = await Get(Approver, $"/api/v1/runs/{Run}/sign-off/open-matters?severity=CRITICAL&blocking=false&includeTotal=true");
        Assert.Equal(17, critical.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Approver.GetAsync($"/api/v1/runs/{Run}/sign-off/open-matters?severity=URGENT")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Approver.GetAsync("/api/v1/runs/NOPE/sign-off/open-matters")).StatusCode);
    }

    [Fact]
    public async Task Safeguarding_position_lists_the_top_lines_per_currency_and_counts_all_of_them()
    {
        var (b, size) = await Get(Approver, $"/api/v1/runs/{Run}/safeguarding");
        Assert.True(size < 100_000, $"safeguarding response is {size} bytes");
        Assert.Equal(50, b.GetProperty("obligations").GetArrayLength());
        Assert.Equal(50, b.GetProperty("eligibleBalances").GetArrayLength());
        var gbp = b.GetProperty("positions").EnumerateArray().Single(p => p.GetProperty("currency").GetString() == "GBP");
        Assert.Equal(Lines, gbp.GetProperty("obligationLinesTotal").GetInt32());
        Assert.Equal(Lines, gbp.GetProperty("balanceLinesTotal").GetInt32());
        Assert.Equal("1000.00", Decimal(gbp.GetProperty("obligation")));
        // largest absolute amount first
        var amounts = b.GetProperty("obligations").EnumerateArray().Select(l => decimal.Parse(l.GetProperty("amount").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(amounts.OrderByDescending(a => a), amounts);
        var balances = b.GetProperty("eligibleBalances").EnumerateArray().ToList();
        Assert.Equal("EXT-03000", balances[0].GetProperty("externalAccountId").GetString());
        Assert.True(balances[0].GetProperty("included").GetBoolean());

        var (small, _) = await Get(Approver, $"/api/v1/runs/{Run}/safeguarding?linesLimit=3");
        Assert.Equal(3, small.GetProperty("obligations").GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Approver.GetAsync($"/api/v1/runs/{Run}/safeguarding?linesLimit=500")).StatusCode);
    }

    private static string Decimal(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("obligation")]
    [InlineData("balance")]
    public async Task Safeguarding_lines_page_through_the_whole_list_without_gaps_or_repeats(string kind)
    {
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (b, size) = await Get(Approver, $"/api/v1/runs/{Run}/safeguarding/GBP/lines?kind={kind}&limit=200&includeTotal=true" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.True(size < 200_000, $"lines page is {size} bytes");
            Assert.Equal(Lines, b.GetProperty("page").GetProperty("total").GetInt32());
            Assert.Equal(kind, b.GetProperty("kind").GetString());
            seen.AddRange(kind == "obligation"
                ? b.GetProperty("obligationLines").EnumerateArray().Select(l => l.GetProperty("accountId").GetString()!)
                : b.GetProperty("balanceLines").EnumerateArray().Select(l => l.GetProperty("sourceRowId").GetString()!));
            Assert.Equal(0, b.GetProperty(kind == "obligation" ? "balanceLines" : "obligationLines").GetArrayLength());
            cursor = b.GetProperty("page").TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            pages++;
        } while (cursor is not null && pages < 100);
        Assert.Equal(Lines, seen.Count);
        Assert.Equal(Lines, seen.Distinct().Count());
    }

    [Fact]
    public async Task Safeguarding_lines_search_by_account_and_reject_bad_requests()
    {
        var (o, _) = await Get(Approver, $"/api/v1/runs/{Run}/safeguarding/GBP/lines?kind=obligation&search=ca-0001&includeTotal=true");
        Assert.Equal(10, o.GetProperty("page").GetProperty("total").GetInt32()); // CA-00010 .. CA-00019
        var (bal, _) = await Get(Approver, $"/api/v1/runs/{Run}/safeguarding/GBP/lines?kind=balance&search=EXT-0299&includeTotal=true");
        Assert.Equal(10, bal.GetProperty("page").GetProperty("total").GetInt32());
        var (none, _) = await Get(Approver, $"/api/v1/runs/{Run}/safeguarding/GBP/lines?kind=obligation&search=%25");
        Assert.Equal(0, none.GetProperty("obligationLines").GetArrayLength()); // the wildcard is matched literally

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Approver.GetAsync($"/api/v1/runs/{Run}/safeguarding/GBP/lines")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Approver.GetAsync($"/api/v1/runs/{Run}/safeguarding/GBP/lines?kind=other")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Approver.GetAsync($"/api/v1/runs/{Run}/safeguarding/EUR/lines?kind=obligation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Approver.GetAsync("/api/v1/runs/NOPE/safeguarding/GBP/lines?kind=obligation")).StatusCode);
    }

    [Fact]
    public async Task Control_room_stays_small_and_counts_what_it_truncates()
    {
        var (b, size) = await Get(Approver, $"/api/v1/runs/{Run}/control-room");
        Assert.True(size < 200_000, $"control-room response is {size} bytes");
        Assert.Equal(10, b.GetProperty("attentionQueue").GetArrayLength());
        var delivery = b.GetProperty("evidenceReadiness").EnumerateArray().Single(d => d.GetProperty("deliveryId").GetString() == "DLV-SZ1");
        Assert.Equal(20, delivery.GetProperty("findingIds").GetArrayLength());
        Assert.Equal(Exceptions, delivery.GetProperty("findingCount").GetInt32());
        var gbp = b.GetProperty("safeguarding").EnumerateArray().Single(p => p.GetProperty("currency").GetString() == "GBP");
        Assert.Equal(20, gbp.GetProperty("exceptionIds").GetArrayLength());
        Assert.Equal(40, gbp.GetProperty("exceptionCount").GetInt32());
        Assert.False(gbp.TryGetProperty("obligationLinesTotal", out _));
    }
}
