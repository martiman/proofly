using System.Net;
using System.Text.Json;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Exceptions;

/// <summary>Contract batch 3: summary/list filter parity, assignable users, approval kind.</summary>
public class ContractBatch3Tests(ExceptionsFixture f) : IClassFixture<ExceptionsFixture>
{
    private HttpClient Viewer => f.As(f.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static readonly string[][] FilterPool =
    [
        ["severity=CRITICAL", "severity=HIGH,MEDIUM", "severity=LOW", "severity=CRITICAL,HIGH,MEDIUM,LOW"],
        ["status=OPEN", "status=IN_INVESTIGATION,WAITING_FOR_DATA", "status=RESOLVED", "status=OPEN,RESOLVED"],
        ["control=matching", "control=independent_ledger,data_completeness", "control=safeguarding"],
        ["blocking=true", "blocking=false"],
        ["ownerId=u1", "ownerId=u2", "unassigned=true", "unassigned=false"],
        ["currency=GBP", "currency=EUR", "currency=JPY"],
        ["ageDaysMin=1", "ageDaysMin=3", "ageDaysMax=2", "ageDaysMax=100"],
        ["businessDateFrom=2026-10-26", "businessDateFrom=2026-10-27", "businessDateTo=2026-10-26", "businessDateTo=2026-10-27"],
        ["dueBefore=2026-10-29T00:00:00Z", "dueBefore=2026-11-30T00:00:00Z"],
        ["search=transfers", "search=mismatch", "search=row-1", "search=zzz-no-match"],
        ["savedViewId=V-1"],
    ];

    [Fact]
    public async Task Summary_total_equals_the_list_total_for_random_filter_combinations()
    {
        var rnd = new Random(20261028);
        var checkedCombos = 0;
        for (var i = 0; i < 80; i++)
        {
            var parts = new List<string>();
            foreach (var group in FilterPool)
            {
                if (rnd.Next(3) == 0) { parts.Add(group[rnd.Next(group.Length)]); }
            }
            // At most one parameter per name; a value range needs a currency (the same rule applies to both operations).
            parts = parts.GroupBy(p => p.Split('=')[0]).Select(g => g.First()).ToList();
            if (parts.Any(p => p.StartsWith("unassigned=true")) && parts.Any(p => p.StartsWith("ownerId="))) { parts.RemoveAll(p => p.StartsWith("ownerId=")); }
            var ccy = parts.FirstOrDefault(p => p.StartsWith("currency="));
            if (ccy is not null && rnd.Next(2) == 0) { parts.Add("valueMin=" + new[] { "0", "10", "100", "1500" }[rnd.Next(4)]); }
            if (ccy is not null && rnd.Next(2) == 0) { parts.Add("valueMax=" + new[] { "20", "500", "60000" }[rnd.Next(3)]); }
            var q = string.Join('&', parts);

            var list = await Viewer.GetAsync($"/api/v1/runs/R1/exceptions?includeTotal=true&limit=1&{q}");
            var summary = await Viewer.GetAsync($"/api/v1/runs/R1/exceptions/summary?{q}");
            Assert.True(list.StatusCode == summary.StatusCode, $"'{q}': list {(int)list.StatusCode} summary {(int)summary.StatusCode}");
            if (list.StatusCode != HttpStatusCode.OK) { continue; }
            var total = (await Json(list)).GetProperty("page").GetProperty("total").GetInt32();
            var s = await Json(summary);
            Assert.True(total == s.GetProperty("total").GetInt32(), $"'{q}': list total {total}, summary total {s.GetProperty("total").GetInt32()}");
            Assert.Equal(total, s.GetProperty("bySeverity").EnumerateObject().Sum(p => p.Value.GetInt32()));
            Assert.Equal(total, s.GetProperty("byStatus").EnumerateObject().Sum(p => p.Value.GetInt32()));
            Assert.Equal(total, s.GetProperty("byControl").EnumerateObject().Sum(p => p.Value.GetInt32()));
            Assert.Equal(total, s.GetProperty("byReason").EnumerateArray().Sum(r => r.GetProperty("count").GetInt32()));
            checkedCombos++;
        }
        Assert.True(checkedCombos > 40);
    }

    [Theory]
    [InlineData("valueMin=10")]
    [InlineData("severity=NOPE")]
    [InlineData("blocking=maybe")]
    [InlineData("ageDaysMin=-1")]
    [InlineData("dueBefore=tomorrow")]
    [InlineData("unassigned=true&ownerId=u1")]
    public async Task Summary_rejects_the_same_invalid_filters_as_the_list(string query)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R1/exceptions?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Viewer.GetAsync("/api/v1/runs/R1/exceptions/summary?" + query)).StatusCode);
    }

    [Fact]
    public async Task Summary_accepts_blocking_value_range_age_dates_due_and_search_filters()
    {
        string Q(string q) => "/api/v1/runs/R1/exceptions/summary?" + q;
        Assert.Equal(2, (await Json(await Viewer.GetAsync(Q("blocking=true")))).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Json(await Viewer.GetAsync(Q("currency=EUR&valueMin=1500")))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await Viewer.GetAsync(Q("currency=EUR&valueMin=1500&valueMax=1600")))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await Viewer.GetAsync(Q("search=ledger")))).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Json(await Viewer.GetAsync(Q("businessDateFrom=2026-10-27&blocking=true")))).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Json(await Viewer.GetAsync(Q("dueBefore=2026-10-27T10:00:00Z")))).GetProperty("total").GetInt32());
    }

    // ---- listAssignableUsers --------------------------------------------------------------------------------------------

    private static string[] Ids(JsonElement arr) => arr.EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task Assignable_users_are_active_investigators_of_the_entity_and_need_exception_assign()
    {
        var dir = new UserDirectory(f.Db);
        await dir.EnsureUserAsync(new UserRecord("u-le1-analyst", "Zed Analyst", "z@x.test", "sub-z", true, [new RoleAssignment("ANALYST", "LE-1")]));
        await dir.EnsureUserAsync(new UserRecord("u-le2-analyst", "Other Entity", "o@x.test", "sub-o", true, [new RoleAssignment("ANALYST", "LE-2")]));
        await dir.EnsureUserAsync(new UserRecord("u-inactive", "Inactive Analyst", "i@x.test", "sub-i", false, [new RoleAssignment("ANALYST", "LE-1")]));
        await dir.EnsureUserAsync(new UserRecord("u-le1-viewer", "Only Viewer", "v@x.test", "sub-v", true, [new RoleAssignment("VIEWER", "LE-1")]));

        var analyst = f.As(f.TokenFor(Role.Analyst));
        var res = await analyst.GetAsync("/api/v1/legal-entities/LE-1/assignable-users");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Json(res);
        var ids = Ids(body);
        Assert.Contains("u-le1-analyst", ids);
        Assert.Contains("local-analyst", ids);   // all-entities analyst
        Assert.Contains("local-approver", ids);  // the approver bundle includes exception.investigate
        Assert.DoesNotContain("u-le2-analyst", ids);
        Assert.DoesNotContain("u-inactive", ids);
        Assert.DoesNotContain("u-le1-viewer", ids);
        Assert.DoesNotContain("local-viewer", ids);
        Assert.DoesNotContain("local-config-admin", ids);
        var names = body.EnumerateArray().Select(u => u.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal).ToArray(), names);
        Assert.Equal(["ANALYST"], body.EnumerateArray().Single(u => u.GetProperty("id").GetString() == "u-le1-analyst").GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Contains("u-le2-analyst", Ids(await Json(await analyst.GetAsync("/api/v1/legal-entities/LE-2/assignable-users"))));
        Assert.DoesNotContain("u-le1-analyst", Ids(await Json(await analyst.GetAsync("/api/v1/legal-entities/LE-2/assignable-users"))));
    }

    [Fact]
    public async Task Assignable_users_enforce_permission_scope_and_existence()
    {
        const string url = "/api/v1/legal-entities/LE-1/assignable-users";
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.As(null).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.GetAsync(url)).StatusCode);                          // exception.view is not enough
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(f.TokenFor(Role.ConfigAdmin)).GetAsync(url)).StatusCode); // settings.view is not enough
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.TokenFor(Role.Analyst)).GetAsync("/api/v1/legal-entities/NOPE/assignable-users")).StatusCode);
        var scoped = f.As(await f.ScopedToken("scoped-le2", Role.Analyst, "LE-2"));
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.GetAsync(url)).StatusCode);   // outside the caller's entity access
        Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync("/api/v1/legal-entities/LE-2/assignable-users")).StatusCode);
    }

    // ---- Approval.kind ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolution_approvals_carry_the_proposed_kind_and_other_approvals_do_not()
    {
        await f.Exec("""
            INSERT INTO resolution_proposals (id, exception_id, kind, rationale, proposed_by_user_id, status, version)
              VALUES ('RP-K1','E-002','ACCEPTED_DIFFERENCE','Within appetite','u1','PENDING',1);
            INSERT INTO approvals (id, object_type, object_id, run_id, proposal_version, proposer_user_id, required_role, status, evidence_refs, policy_version, proposed_at, rationale)
              VALUES ('AP-K1','EXCEPTION_RESOLUTION','RP-K1','R1',1,'u1','APPROVER','PENDING','["ev-1","ev-2"]','pol-1','2026-10-27T14:00:00Z','Within appetite'),
                     ('AP-K2','MANUAL_MATCH','MG-X','R1',1,'u1','APPROVER','PENDING','[]','pol-1','2026-10-27T15:00:00Z',NULL);
            UPDATE exceptions SET status = 'PENDING_APPROVAL' WHERE id = 'E-002';
            """);
        var d = await Json(await f.As(f.TokenFor(Role.Approver)).GetAsync("/api/v1/exceptions/E-002"));
        var pending = d.GetProperty("pendingApproval");
        Assert.Equal("ACCEPTED_DIFFERENCE", pending.GetProperty("kind").GetString());
        Assert.Equal("Within appetite", pending.GetProperty("rationale").GetString());
        Assert.Equal(["ev-1", "ev-2"], pending.GetProperty("evidenceRefs").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("ACCEPTED_DIFFERENCE", d.GetProperty("approvals")[0].GetProperty("kind").GetString());

        var list = await Json(await f.As(f.TokenFor(Role.Approver)).GetAsync("/api/v1/approvals?limit=50"));
        var byId = list.GetProperty("items").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal("ACCEPTED_DIFFERENCE", byId["AP-K1"].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, byId["AP-K2"].GetProperty("kind").ValueKind);
    }
}
