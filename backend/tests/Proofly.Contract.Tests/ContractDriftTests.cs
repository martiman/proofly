using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Domain.Enums;

namespace Proofly.Contract.Tests;

/// <summary>
/// The contract (docs/integrations/api/openapi.yaml) and the running API must agree: every operation is routed or explicitly
/// allow-listed in unimplemented-operations.txt, and the declared x-required-permission is enforced.
/// </summary>
[Collection(ContractApiCollection.Name)]
public class ContractDriftTests(ContractApiFixture f)
{
    private static string Normalise(string path) => Regex.Replace(path.TrimEnd('/'), @"\{[^}]+\}", "{}");

    private static HashSet<string> AllowList()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "unimplemented-operations.txt"));
        return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
    }

    private HashSet<string> MappedRoutes()
    {
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in f.F.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var m in methods) { mapped.Add($"{m.ToUpperInvariant()} {Normalise(Regex.Replace(e.RoutePattern.RawText ?? "", @"\{([^}:]+)[^}]*\}", "{$1}"))}"); }
        }
        return mapped;
    }

    private bool IsMapped(Op op, HashSet<string> mapped) => mapped.Contains($"{op.Method} {Normalise(op.Path)}");

    [Fact]
    public void Every_operation_declares_the_metadata_the_platform_relies_on()
    {
        Assert.NotEmpty(ContractDoc.Ops);
        Assert.Equal(ContractDoc.Ops.Count, ContractDoc.Ops.Select(o => o.OperationId).Distinct().Count());
        Assert.Equal(ContractDoc.Ops.Count, ContractDoc.Ops.Select(o => o.Method + " " + Normalise(o.Path)).Distinct().Count());
        var permissions = ContractDoc.EnumOf("Permission");
        foreach (var op in ContractDoc.Ops)
        {
            Assert.True(op.Permission is not null, $"{op.OperationId} has no x-required-permission.");
            Assert.True(permissions.Contains(op.Permission), $"{op.OperationId}: unknown permission '{op.Permission}'.");
            Assert.All(op.RequiredHeaders, h => Assert.Contains(h, new[] { "If-Match", "Idempotency-Key" }));
        }
    }

    [Fact]
    public void Every_operation_is_routed_or_allow_listed_and_the_allow_list_has_no_stale_entries()
    {
        var mapped = MappedRoutes();
        var allow = AllowList();
        var ids = ContractDoc.Ops.Select(o => o.OperationId).ToHashSet();

        var unknown = allow.Where(a => !ids.Contains(a)).ToList();
        Assert.True(unknown.Count == 0, "unimplemented-operations.txt lists operations that are not in the contract: " + string.Join(", ", unknown));

        var missing = ContractDoc.Ops.Where(o => !IsMapped(o, mapped) && !allow.Contains(o.OperationId)).Select(o => $"{o.OperationId} ({o.Method} {o.Path})").ToList();
        Assert.True(missing.Count == 0, "Contract operations without a route or allow-list entry: " + string.Join("; ", missing));

        var implemented = ContractDoc.Ops.Where(o => IsMapped(o, mapped) && allow.Contains(o.OperationId)).Select(o => o.OperationId).ToList();
        Assert.True(implemented.Count == 0, "Implemented operations still in unimplemented-operations.txt (remove them): " + string.Join(", ", implemented));
    }

    [Fact]
    public async Task Routed_operations_answer_a_valid_request_with_something_other_than_route_not_found()
    {
        var mapped = MappedRoutes();
        var client = f.Everything();
        foreach (var op in ContractDoc.Ops.Where(o => IsMapped(o, mapped)))
        {
            var res = await client.SendAsync(Requests.Build(op, query: Requests.RequiredQuery(op)));
            // A 404 for a missing resource carries the contract's not-found problem; an unrouted path has no endpoint at all.
            if (res.StatusCode == HttpStatusCode.NotFound) { Assert.Contains("not-found", await res.Content.ReadAsStringAsync()); }
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        }
    }

    public static IEnumerable<object[]> Ops() => ContractDoc.OpData();

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task Declared_permission_is_enforced(string operationId)
    {
        var op = ContractDoc.ById(operationId);
        if (!IsMapped(op, MappedRoutes())) { Assert.Contains(operationId, AllowList()); return; }

        // Unauthenticated: 401.
        var anonymous = await f.Client(null).SendAsync(Requests.Build(op));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        if (op.Permission == WireNames.ToPermission(Permission.AuthSelf)) { return; } // every authenticated caller

        // Every permission except the required one: never 200, and specifically 403 (or 404 where existence must not leak).
        var others = ContractApiFixture.AllPermissions.Where(p => p != op.Permission);
        var res = await f.Client("lacking", others).SendAsync(Requests.Build(op, query: Requests.RequiredQuery(op)));
        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"{op.OperationId} ({op.Permission}) answered {(int)res.StatusCode} to a caller without the permission.");
    }
}
