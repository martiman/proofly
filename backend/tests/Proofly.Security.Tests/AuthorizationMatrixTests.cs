using System.Net;
using Proofly.Domain.Auth;
using Proofly.Domain.Enums;
using Proofly.Security.Tests.Support;

namespace Proofly.Security.Tests;

/// <summary>Issue #52 (6): the authorization matrix is generated from the contract (x-required-permission) and the role bundles.</summary>
public sealed class AuthorizationMatrixTests : IClassFixture<SeededDb>, IAsyncLifetime
{
    private readonly SeededDb _db;
    private SecFactory _f = null!;
    private Dictionary<string, string> _a = null!;

    public AuthorizationMatrixTests(SeededDb db) => _db = db;

    public async Task InitializeAsync()
    {
        _f = new SecFactory(_db);
        _ = _f.Server;
        _a = await _db.IdsOf("A");
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    /// <summary>The only contract operation without a route today (issue: lineage API). Anything else missing is a regression.</summary>
    private static readonly HashSet<string> KnownUnimplemented = ["GET /api/v1/lineage/{subjectKind}/{subjectId}"];

    private IEnumerable<Proofly.Api.Platform.Contract.OperationSpec> Implemented(IEnumerable<Proofly.Api.Platform.Contract.OperationSpec> ops)
    {
        var missing = _f.UnimplementedOperations();
        return ops.Where(o => !missing.Contains(o.Method + " /api/v1" + o.Template));
    }

    [Fact]
    public void Only_known_contract_operations_lack_a_route()
    {
        var unexpected = _f.UnimplementedOperations().Except(KnownUnimplemented).ToList();
        Assert.True(unexpected.Count == 0, "Contract operations without a route: " + string.Join(", ", unexpected));
    }

    private string Id(string p) => _a.TryGetValue(p, out var v) ? v : "x-" + p;
    private static bool Holds(Role r, string wire) => RolePermissions.For(r).Any(p => WireNames.ToPermission(p) == wire);

    [Fact]
    public void Every_contract_permission_is_a_known_permission_and_every_operation_declares_one()
    {
        var known = Enum.GetValues<Permission>().Select(WireNames.ToPermission).ToHashSet();
        Assert.All(Contract.Operations, o => Assert.True(o.RequiredPermission is not null, $"{o.Method} {o.Template} has no x-required-permission"));
        Assert.All(Contract.Operations, o => Assert.Contains(o.RequiredPermission!, known));
    }

    [Fact]
    public async Task A_caller_without_the_permission_gets_403_on_every_operation_for_every_role()
    {
        var failures = new List<string>();
        var denied = 0;
        foreach (var role in RolePermissions.Roles)
        {
            var client = _f.As(role);
            foreach (var op in Implemented(Contract.WithPermission).Where(o => o.RequiredPermission != "auth.self" && !Holds(role, o.RequiredPermission!)))
            {
                using var req = Contract.Request(op, Id);
                var res = await client.SendAsync(req);
                denied++;
                if (res.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
                {
                    failures.Add($"{role} {op.Method} {op.Template} ({op.RequiredPermission}) -> {(int)res.StatusCode}");
                }
                else if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    failures.Add($"{role} {op.Method} {op.Template} -> 404 for a caller who is in scope: expected 403");
                }
            }
        }
        Assert.True(denied > 100);
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401_on_every_operation()
    {
        var client = _f.As((string?)null);
        foreach (var op in Implemented(Contract.Operations))
        {
            using var req = Contract.Request(op, Id);
            var res = await client.SendAsync(req);
            Assert.True(res.StatusCode == HttpStatusCode.Unauthorized, $"{op.Method} {op.Template} -> {(int)res.StatusCode}");
        }
    }

    [Fact]
    public void Viewer_holds_no_permission_for_any_mutating_operation_except_personal_views_and_evidence_export()
    {
        // Evidence packs are exports (read-only reviewer may export per roles-and-approvals.md); saved views are per-user preferences.
        var allowed = new HashSet<string> { "POST /exception-views", "DELETE /exception-views/{viewId}", "POST /runs/{runId}/evidence-pack" };
        var offenders = Contract.Operations.Where(Contract.IsMutating).Where(o => Holds(Role.Viewer, o.RequiredPermission!))
            .Select(o => $"{o.Method} {o.Template}").Where(s => !allowed.Contains(s)).ToList();
        Assert.True(offenders.Count == 0, "VIEWER can call: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task Viewer_gets_403_on_mutating_operations_that_change_financial_state()
    {
        var client = _f.As(Role.Viewer);
        foreach (var op in Contract.Operations.Where(Contract.IsMutating).Where(o => !Holds(Role.Viewer, o.RequiredPermission!)))
        {
            using var req = Contract.Request(op, Id);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(req)).StatusCode);
        }
    }

    [Fact]
    public void Analyst_cannot_approve_anything()
    {
        var approvals = Contract.Operations.Where(o => IsApproval(o.RequiredPermission!)).ToList();
        Assert.NotEmpty(approvals);
        Assert.All(approvals, o => Assert.False(Holds(Role.Analyst, o.RequiredPermission!), $"ANALYST holds {o.RequiredPermission} ({o.Template})"));
        Assert.All(approvals, o => Assert.False(Holds(Role.Viewer, o.RequiredPermission!)));
    }

    private static bool IsApproval(string p) => p.EndsWith(".approve", StringComparison.Ordinal) || p == "signoff.decide";

    [Fact]
    public void Each_approval_permission_is_held_only_by_its_documented_role()
    {
        // docs/security/roles-and-approvals.md and ADR 0005: financial approvals belong to the control owner (APPROVER),
        // configuration approval to CONFIG_ADMIN, access administration to SECURITY_ADMIN.
        var documented = new Dictionary<string, Role[]>
        {
            ["exception.approve"] = [Role.Approver],
            ["safeguarding.adjustment.approve"] = [Role.Approver],
            ["signoff.decide"] = [Role.Approver],
            ["settings.approve"] = [Role.ConfigAdmin],
            ["access.manage"] = [Role.SecurityAdmin],
        };
        var inContract = Contract.Operations.Select(o => o.RequiredPermission!).Where(IsApproval).Distinct().ToList();
        Assert.All(inContract, p => Assert.True(documented.ContainsKey(p), $"Approval permission {p} has no documented holder; document it in this test and in roles-and-approvals.md"));
        foreach (var (perm, roles) in documented)
        {
            var holders = RolePermissions.Roles.Where(r => Holds(r, perm)).Order().ToArray();
            Assert.Equal(roles.Order().ToArray(), holders);
        }
        // Separation of duties between roles: nobody who administers access or configuration decides financial controls.
        Assert.False(Holds(Role.SecurityAdmin, "signoff.decide"));
        Assert.False(Holds(Role.ConfigAdmin, "signoff.decide"));
        Assert.False(Holds(Role.ConfigAdmin, "exception.approve"));
    }

    [Fact]
    public async Task Role_bundles_are_what_the_api_reports_in_me()
    {
        foreach (var role in RolePermissions.Roles)
        {
            var body = await _f.As(role).GetStringAsync("/api/v1/me");
            foreach (var p in Enum.GetValues<Permission>().Select(WireNames.ToPermission))
            {
                Assert.Equal(Holds(role, p), body.Contains($"\"{p}\""));
            }
        }
    }
}
