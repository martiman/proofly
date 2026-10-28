using System.Net;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (7): a caller scoped to entity A can never read or mutate entity B resources, for every contract operation that takes an id.</summary>
public sealed class EntityIsolationTests : IClassFixture<SeededDb>, IAsyncLifetime
{
    private readonly SeededDb _db;
    private SecFactory _f = null!;
    private string _scoped = "";
    private Dictionary<string, string> _b = null!, _a = null!;

    public EntityIsolationTests(SeededDb db) => _db = db;

    public async Task InitializeAsync()
    {
        _f = new SecFactory(_db);
        _ = _f.Server;
        // Holds every permission in the system, but only for entity A.
        _scoped = await _db.UserWith(_f, "scoped-a", ("APPROVER", "LE-SECA"), ("CONFIG_ADMIN", "LE-SECA"), ("SECURITY_ADMIN", "LE-SECA"));
        _a = await _db.IdsOf("A");
        _b = await _db.IdsOf("B");
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private static IEnumerable<Proofly.Api.Platform.Contract.OperationSpec> IdOperations() =>
        Contract.WithPermission.Where(o => Contract.PathParams(o).Count > 0 && !Contract.PathParams(o).Any(SeededDb.GlobalIds.Contains));

    private static string Value(Dictionary<string, string> ids, string name) => ids.TryGetValue(name, out var v) ? v : "unmapped-" + name;

    [Fact]
    public void The_scoped_caller_holds_every_permission_the_contract_asks_for()
    {
        var granted = Enum.GetValues<Role>().SelectMany(r => Proofly.Domain.Auth.RolePermissions.For(r)).Select(WireNames.ToPermission).ToHashSet();
        var missing = Contract.WithPermission.Select(o => o.RequiredPermission!).Where(p => !granted.Contains(p)).Distinct().ToList();
        Assert.True(missing.Count == 0, "No role grants: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_id_parameter_in_the_contract_is_mapped_to_a_seeded_resource_or_declared_global()
    {
        var unmapped = IdOperations().SelectMany(Contract.PathParams).Where(p => !_b.ContainsKey(p)).Distinct().ToList();
        Assert.True(unmapped.Count == 0, "Add a seeded resource for path parameter(s) " + string.Join(", ", unmapped) + " in SeededDb.IdsOf (or SeededDb.GlobalIds if not entity-owned).");
    }

    // downloadEvidencePack: 404 until a pack exists (covered in EvidenceAuditTests); getLineage: not yet implemented in the API.
    private static readonly HashSet<string> NoSeedNeeded = ["downloadEvidencePack", "getLineage"];

    [Fact]
    public async Task Control_the_seeded_entity_B_resources_exist_for_a_caller_in_scope_of_everything()
    {
        // Without this the 404 assertions below could pass for the wrong reason (resource missing instead of out of scope).
        var admin = _f.As(Role.Approver);
        var notFound = new List<string>();
        foreach (var op in IdOperations().Where(o => o.Method == "GET"))
        {
            var res = await admin.GetAsync(Contract.Url(op, p => Value(_b, p), true, p => Query(_b, p)));
            if (res.StatusCode == HttpStatusCode.NotFound && !NoSeedNeeded.Contains(op.OperationId)) { notFound.Add($"{op.OperationId} {op.Template}"); }
        }
        Assert.True(notFound.Count == 0, "Seed does not resolve for: " + string.Join("; ", notFound));
    }

    [Fact]
    public async Task Scoped_caller_gets_404_and_no_entity_B_data_on_every_id_operation_and_nothing_is_changed()
    {
        var client = _f.As(_scoped);
        var tables = new[] { "runs", "exceptions", "resolution_proposals", "match_groups", "match_candidates", "safeguarding_adjustments", "approvals",
            "audit_events", "evidence_packs", "jobs", "exception_notes", "exception_assignments", "run_signoffs", "source_deliveries" };
        var before = await _db.Snapshot(tables);
        var failures = new List<string>();
        foreach (var op in IdOperations())
        {
            using var req = Contract.Request(op, p => Value(_b, p), p => Query(_b, p));
            var res = await client.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (res.StatusCode != HttpStatusCode.NotFound) { failures.Add($"{op.Method} {op.Template} -> {(int)res.StatusCode}: {Trim(body)}"); }
            else if (body.Contains("Entity SECB") || body.Contains("Unmatched SECB") || body.Contains("REF-SECB")) { failures.Add($"{op.Method} {op.Template} leaked entity B data"); }
        }
        Assert.True(failures.Count == 0, "Entity B resources reachable by an entity A caller:\n" + string.Join('\n', failures));
        Assert.Equal(before, await _db.Snapshot(tables));
    }

    [Fact]
    public async Task Control_the_same_scoped_caller_can_read_entity_A_resources()
    {
        var client = _f.As(_scoped);
        foreach (var op in IdOperations().Where(o => o.Method == "GET" && !NoSeedNeeded.Contains(o.OperationId)))
        {
            var res = await client.GetAsync(Contract.Url(op, p => Value(_a, p), true, p => Query(_a, p)));
            Assert.True(res.StatusCode != HttpStatusCode.NotFound && res.StatusCode != HttpStatusCode.Forbidden && (int)res.StatusCode < 500,
                $"{op.OperationId} {op.Template} -> {(int)res.StatusCode}");
        }
    }

    [Fact]
    public async Task List_endpoints_never_contain_entity_B_data_for_the_scoped_caller()
    {
        var client = _f.As(_scoped);
        foreach (var op in Contract.WithPermission.Where(o => o.Method == "GET" && Contract.PathParams(o).Count == 0))
        {
            var res = await client.GetAsync(Contract.Url(op, p => p));
            var body = await res.Content.ReadAsStringAsync();
            Assert.True((int)res.StatusCode < 500, $"{op.Template} -> {(int)res.StatusCode}");
            Assert.False(body.Contains(SeededDb.MarkerB), $"{op.Template} leaks entity B data");
        }
        // Run-scoped lists of the caller's own run must not include B rows either (ids and amounts of entity B are EUR-only).
        foreach (var op in IdOperations().Where(o => o.Method == "GET" && Contract.PathParams(o).SequenceEqual(["runId"])))
        {
            var res = await client.GetAsync(Contract.Url(op, p => Value(_a, p), true, p => Query(_a, p)));
            Assert.DoesNotContain(SeededDb.MarkerB, await res.Content.ReadAsStringAsync());
        }
    }

    /// <summary>Query parameters that carry a resource id (compare: against).</summary>
    private static string? Query(Dictionary<string, string> ids, string name) => name switch { "against" or "runId" => ids["runId"], _ => null };

    [Fact]
    public async Task Comparing_the_callers_own_run_with_a_run_of_another_entity_is_refused()
    {
        var res = await _f.As(_scoped).GetAsync($"/api/v1/runs/{_a["runId"]}/compare?against={_b["runId"]}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(SeededDb.MarkerB, (await res.Content.ReadAsStringAsync()).Replace(_b["runId"], ""));
    }

    private static string Trim(string s) => s.Length > 160 ? s[..160] : s;
}
