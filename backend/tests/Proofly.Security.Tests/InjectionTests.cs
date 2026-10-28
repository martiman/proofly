using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proofly.Api.Platform.Contract;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (8): SQL injection and over-posting smoke on every filter, search, sort, cursor and id parameter of the contract.</summary>
public sealed class InjectionTests : IClassFixture<SeededDb>, IAsyncLifetime
{
    private static readonly string[] Hostile =
    [
        "'; DROP TABLE exceptions; --",
        "' OR '1'='1",
        "\" OR \"\"=\"",
        "%' OR 1=1 --",
        "1; UPDATE runs SET state='SIGNED_OFF'; --",
        "'||(SELECT pg_sleep(5))||'",
        "1) UNION SELECT usename, passwd, NULL FROM pg_shadow --",
        "\\' OR 1=1 --",
        "-severity; DELETE FROM exceptions",
        "%", "_", "\\",
        "{{7*7}}", "${jndi:ldap://x}", "../../etc/passwd",
        "é‮", new string('A', 300),
    ];

    private static readonly string[] Tables =
        ["runs", "exceptions", "resolution_proposals", "match_groups", "safeguarding_adjustments", "approvals", "audit_events", "source_deliveries",
         "transfer_instructions", "bank_transactions", "ledger_entries", "account_balances", "users", "user_roles", "config_rules", "config_changes", "saved_views"];

    private readonly SeededDb _db;
    private SecFactory _f = null!;
    private string _scopedA = "";
    private Dictionary<string, string> _a = null!;

    public InjectionTests(SeededDb db) => _db = db;

    public async Task InitializeAsync()
    {
        _f = new SecFactory(_db);
        _ = _f.Server;
        _scopedA = await _db.UserWith(_f, "inj-scoped-a", ("APPROVER", "LE-SECA"), ("CONFIG_ADMIN", "LE-SECA"));
        _a = await _db.IdsOf("A");
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private static bool IsFreeText(ParameterSpec p) => p.Type == "string" || (p.Type == "array" && p.ItemType == "string");

    private IEnumerable<OperationSpec> Implemented(IEnumerable<OperationSpec> ops)
    {
        var missing = _f.UnimplementedOperations();
        return ops.Where(o => !missing.Contains(o.Method + " /api/v1" + o.Template));
    }

    [Fact]
    public void The_contract_has_search_and_sort_parameters_to_test()
    {
        var names = Contract.Operations.SelectMany(o => o.Parameters).Where(p => p.In == "query" && IsFreeText(p)).Select(p => p.Name).Distinct().ToList();
        Assert.Contains("search", names);
        Assert.Contains("sort", names);
        Assert.Contains("cursor", names);
    }

    [Fact]
    public async Task Hostile_values_in_every_string_query_parameter_never_cause_a_500_slow_query_data_change_or_cross_entity_data()
    {
        var before = await _db.Snapshot(Tables);
        var client = _f.As(_scopedA);
        var failures = new List<string>();
        var sent = 0;
        foreach (var op in Implemented(Contract.WithPermission.Where(o => o.Method == "GET")))
        {
            foreach (var p in op.Parameters.Where(p => p.In == "query" && IsFreeText(p)))
            {
                foreach (var value in Hostile)
                {
                    var url = Contract.Url(op, n => _a.TryGetValue(n, out var v) ? v : "x", false, n => n == p.Name ? value : n is "against" ? _a["runId"] : null);
                    var sw = Stopwatch.StartNew();
                    var res = await client.GetAsync(url);
                    sw.Stop();
                    sent++;
                    var body = await res.Content.ReadAsStringAsync();
                    if ((int)res.StatusCode >= 500) { failures.Add($"{op.Template}?{p.Name}={Short(value)} -> {(int)res.StatusCode}"); }
                    else if (sw.Elapsed > TimeSpan.FromSeconds(4)) { failures.Add($"{op.Template}?{p.Name}={Short(value)} took {sw.Elapsed.TotalSeconds:F1}s (injected pg_sleep?)"); }
                    else if (body.Contains(SeededDb.MarkerB)) { failures.Add($"{op.Template}?{p.Name}={Short(value)} returned entity B data"); }
                    else if (body.Contains("pg_shadow") || body.Contains("usename")) { failures.Add($"{op.Template}?{p.Name}={Short(value)} echoed database internals"); }
                }
            }
        }
        Assert.True(sent > 100, $"only {sent} requests were sent");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.Equal(before, await _db.Snapshot(Tables));
    }

    [Fact]
    public async Task Hostile_path_ids_are_404_or_422_never_500()
    {
        var client = _f.As(_scopedA);
        var failures = new List<string>();
        foreach (var op in Implemented(Contract.WithPermission.Where(o => Contract.PathParams(o).Count > 0 && !SeededDb.GlobalIds.Overlaps(Contract.PathParams(o)) && o.Method == "GET")))
        {
            foreach (var value in Hostile.Where(v => v.Length < 100 && !v.Contains('/')))
            {
                var res = await client.GetAsync(Contract.Url(op, _ => value, true, n => n is "against" ? _a["runId"] : null));
                if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.OK) { failures.Add($"{op.Template} id={Short(value)} -> {(int)res.StatusCode}"); }
            }
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
        Assert.Equal(1, await _db.Scalar<long>("SELECT count(*) FROM exceptions WHERE id='EX-SECA'"));
    }

    [Fact]
    public async Task A_search_that_is_always_true_returns_no_more_rows_than_the_plain_filter_would()
    {
        var client = _f.As(_scopedA);
        // The search is a literal: a tautology matches nothing in the seeded data (LIKE metacharacters are escaped too).
        foreach (var path in new[] { $"/api/v1/runs/{_a["runId"]}/exceptions", $"/api/v1/runs/{_a["runId"]}/transactions" })
        {
            var baseline = JsonDocument.Parse(await client.GetStringAsync(path)).RootElement.GetProperty("items").GetArrayLength();
            Assert.True(baseline > 0, $"{path} has no seeded rows");
            foreach (var search in new[] { "' OR '1'='1", "%", "_", "\\", "zzzz-no-such-text" })
            {
                var res = await client.GetAsync($"{path}?search={Uri.EscapeDataString(search)}");
                Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path} search={search} -> {(int)res.StatusCode}");
                var n = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength();
                Assert.True(n == 0, $"{path} search={search} returned {n} rows: the search value was interpreted, not matched literally");
            }
        }
    }

    [Fact]
    public async Task Extra_properties_in_request_bodies_never_cause_a_500_or_set_unlisted_fields()
    {
        var client = _f.As(Role.Approver);
        var failures = new List<string>();
        var sent = 0;
        foreach (var op in Implemented(Contract.WithPermission.Where(o => Contract.IsMutating(o) && o.BodySchemaPointer is not null)))
        {
            using var req = Contract.Request(op, n => _a.TryGetValue(n, out var v) ? v : "x");
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync());
            if (body is not JsonObject obj) { continue; }
            foreach (var (k, v) in new Dictionary<string, JsonNode?>
            {
                ["legalEntityId"] = "LE-SECB", ["status"] = "APPROVED", ["actorId"] = "attacker-id", ["userId"] = "attacker-id", ["proposedBy"] = "attacker-id",
                ["roles"] = new JsonArray("APPROVER"), ["__proto__"] = new JsonObject { ["admin"] = true }, ["id"] = "injected-id",
            }) { obj[k] = v?.DeepClone(); }
            using var attack = new HttpRequestMessage(req.Method, req.RequestUri) { Content = new StringContent(obj.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
            foreach (var h in req.Headers) { attack.Headers.TryAddWithoutValidation(h.Key, h.Value); }
            var res = await client.SendAsync(attack);
            sent++;
            if ((int)res.StatusCode >= 500) { failures.Add($"{op.Method} {op.Template} -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()} :: base body {await req.Content!.ReadAsStringAsync()} :: " + string.Join(" // ", _f.Logs.All.Split('\n').Where(l => l.Contains("MessageText") || l.Contains("SqlState") || l.Contains("Unhandled")).Take(3))); }
        }
        Assert.True(sent > 10);
        Assert.True(failures.Count == 0, string.Join('\n', failures));
        foreach (var table in new[] { "audit_events", "approvals", "exceptions", "resolution_proposals", "safeguarding_adjustments", "config_changes", "match_groups", "run_signoffs", "jobs" })
        {
            var text = await _db.Scalar<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(x))::text, '') FROM {table} x");
            Assert.DoesNotContain("attacker-id", text);
            Assert.DoesNotContain("injected-id", text);
        }
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "..." : s;
}
