using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Data;
using Proofly.Data.Auth;
using Proofly.Domain.Enums;

namespace Proofly.Api.Tests.Exceptions.Commands;

public sealed class CommandsFixture : IAsyncLifetime
{
    internal const string Cs = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    internal NpgsqlDataSource Db { get; private set; } = null!;
    internal WebApplicationFactory<Program> Factory { get; private set; } = null!;
    internal LocalTokenService Tokens => Factory.Services.GetRequiredService<LocalTokenService>();

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Cs))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create($"{Cs};Search Path={Schema}");
        await new MigrationRunner(Db).RunAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Proofly", $"{Cs};Search Path={Schema}");
            b.UseSetting("Auth:Mode", "Local");
        });
        _ = Factory.Server; // seeds the local users
        await Exec(Seed.Sql);
        var dir = new UserDirectory(Db);
        await dir.EnsureUserAsync(new UserRecord("u-off", "Off", "off@x.test", "sub-off", false, [new RoleAssignment("ANALYST", "*")]));
        await dir.EnsureUserAsync(new UserRecord("u-norole", "NoRole", "norole@x.test", "sub-norole", true, []));
        await dir.EnsureUserAsync(new UserRecord("u-le2", "Le2", "le2@x.test", "sub-le2", true, [new RoleAssignment("ANALYST", "LE-2")]));
        await dir.EnsureUserAsync(new UserRecord("u-mixed", "Mixed", "mixed@x.test", "sub-mixed", true,
            [new RoleAssignment("VIEWER", "LE-1"), new RoleAssignment("ANALYST", "LE-2")]));
        await dir.EnsureUserAsync(new UserRecord("u-an1", "An1", "an1@x.test", "sub-an1", true, [new RoleAssignment("ANALYST", "LE-1")]));
    }

    public async Task Exec(string sql)
    {
        await using var c = Db.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    public async Task<T?> Scalar<T>(string sql, params (string, object)[] ps)
    {
        await using var c = Db.CreateCommand(sql);
        foreach (var (k, v) in ps) { c.Parameters.AddWithValue(k, v); }
        var o = await c.ExecuteScalarAsync();
        return o is null or DBNull ? default : (T)o;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    public HttpClient As(string token)
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public string TokenFor(Role role) => Tokens.Issue(LocalUsers.Subject(role));
    public string TokenOfSubject(string subject) => Tokens.Issue(subject);

    public async Task<string> ScopedToken(string key, Role role, string legalEntity)
    {
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@x.test", "sub-" + key, true,
            [new RoleAssignment(WireNames.ToUpperSnake(role), legalEntity)]));
        return Tokens.Issue("sub-" + key);
    }
}

internal static class Seed
{
    public static string Sql
    {
        get
        {
            var sb = new StringBuilder("""
                INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-1','One','GBP'),('LE-2','Two','EUR');
                INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state)
                  VALUES ('R1','LE-1','2026-03-02',1,'rs-1','REVIEW_REQUIRED'),('R2','LE-2','2026-03-02',1,'rs-1','REVIEW_REQUIRED'),
                         ('R3','LE-1','2026-03-01',1,'rs-1','SUPERSEDED');
                INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at) VALUES
                  ('X-OTHER','R2','LE-2','2026-03-02','Other entity','matching','NO_BANK_EVIDENCE','HIGH','OPEN',5,'EUR','2026-03-05T09:00Z'),
                  ('X-SUP','R3','LE-1','2026-03-01','Superseded','matching','NO_BANK_EVIDENCE','HIGH','OPEN',1,'GBP','2026-03-05T09:00Z'),
                  ('X-DONE','R1','LE-1','2026-03-02','Closed','matching','NO_BANK_EVIDENCE','LOW','RESOLVED',10,'GBP','2026-03-05T09:00Z'),
                  ('X-PEND','R1','LE-1','2026-03-02','Pending','matching','NO_BANK_EVIDENCE','LOW','PENDING_APPROVAL',10,'JPY','2026-03-05T09:00Z');
                """);
            foreach (var id in new[] { "A1", "A2", "A3", "A4", "A5", "A6", "A7", "N1", "N2", "N3", "E1", "E2", "E3", "E4", "E5", "C1", "C2", "C3", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "I1", "I2" })
            {
                sb.Append($"INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, status, value_amount, value_currency, due_at) VALUES ('{id}','R1','LE-1','2026-03-02','Item {id}','matching','NO_BANK_EVIDENCE','MEDIUM','OPEN',12.5,'GBP','2026-03-05T09:00Z');\n");
            }
            return sb.ToString();
        }
    }
}

public class ExceptionCommandsApiTests(CommandsFixture f) : IClassFixture<CommandsFixture>
{
    private HttpClient Analyst => f.As(f.TokenFor(Role.Analyst));
    private HttpClient Viewer => f.As(f.TokenFor(Role.Viewer));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> EtagOf(HttpClient c, string id) =>
        (await c.GetAsync("/api/v1/exceptions/" + id)).Headers.ETag!.ToString();

    private static HttpRequestMessage Post(string url, object body, string? etag, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (etag is not null) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
        req.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return req;
    }

    private Task<long> Audits(string type, string subject) =>
        f.Scalar<long>("SELECT count(*) FROM audit_events WHERE event_type = @t AND subject_id = @s", ("t", type), ("s", subject));

    [Fact]
    public async Task Assign_sets_owner_returns_new_etag_writes_timeline_assignment_and_audit_together()
    {
        var c = Analyst;
        var etag = await EtagOf(c, "A1");
        var res = await c.SendAsync(Post("/api/v1/exceptions/A1/assign", new { ownerId = "local-approver", note = "Please take this" }, etag));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.NotEqual(etag, res.Headers.ETag!.ToString());
        var body = await Json(res);
        Assert.Equal("local-approver", body.GetProperty("owner").GetProperty("id").GetString());
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("type").GetString() == "ASSIGNMENT");
        Assert.Equal(1, await Audits("EXCEPTION_ASSIGNED", "A1"));
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM exception_assignments WHERE exception_id = 'A1' AND reason = 'Please take this'"));
        Assert.Equal("local-analyst", await f.Scalar<string>("SELECT actor_id FROM audit_events WHERE subject_id = 'A1'"));
        Assert.Equal("Please take this", await f.Scalar<string>("SELECT reason FROM audit_events WHERE subject_id = 'A1'"));
        Assert.Equal("LE-1", await f.Scalar<string>("SELECT chain_id FROM audit_events WHERE subject_id = 'A1'"));
    }

    [Fact]
    public async Task Assign_null_owner_unassigns()
    {
        var c = Analyst;
        var r1 = await c.SendAsync(Post("/api/v1/exceptions/A2/assign", new { ownerId = "local-approver" }, await EtagOf(c, "A2")));
        var r2 = await c.SendAsync(Post("/api/v1/exceptions/A2/assign", new { ownerId = (string?)null }, r1.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(r2)).GetProperty("owner").ValueKind);
        Assert.Equal(2, await Audits("EXCEPTION_ASSIGNED", "A2"));
    }

    [Fact]
    public async Task Missing_If_Match_is_428_and_stale_is_412_without_audit()
    {
        var c = Analyst;
        var missing = await c.SendAsync(Post("/api/v1/exceptions/A3/assign", new { ownerId = "local-approver" }, null));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        var stale = await c.SendAsync(Post("/api/v1/exceptions/A3/assign", new { ownerId = "local-approver" }, "\"v99\""));
        Assert.Equal((HttpStatusCode)412, stale.StatusCode);
        Assert.Equal(0, await Audits("EXCEPTION_ASSIGNED", "A3"));
    }

    [Fact]
    public async Task Idempotent_replay_returns_the_original_response_and_executes_once()
    {
        var c = Analyst;
        var etag = await EtagOf(c, "A4");
        var key = Guid.NewGuid().ToString();
        var first = await c.SendAsync(Post("/api/v1/exceptions/A4/assign", new { ownerId = "local-approver" }, etag, key));
        var replay = await c.SendAsync(Post("/api/v1/exceptions/A4/assign", new { ownerId = "local-approver" }, etag, key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await Audits("EXCEPTION_ASSIGNED", "A4"));
        var other = await c.SendAsync(Post("/api/v1/exceptions/A4/assign", new { ownerId = "local-viewer" }, etag, key));
        Assert.Equal((HttpStatusCode)422, other.StatusCode);
    }

    [Fact]
    public async Task Assign_without_permission_is_403_and_out_of_scope_is_404_and_per_entity_role_is_enforced()
    {
        var viewer = Viewer;
        var etag = await EtagOf(viewer, "A5");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(Post("/api/v1/exceptions/A5/assign", new { ownerId = "local-approver" }, etag))).StatusCode);

        var scoped = f.As(f.TokenOfSubject("sub-le2")); // analyst in LE-2 only
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.SendAsync(Post("/api/v1/exceptions/A5/assign", new { ownerId = "local-approver" }, etag))).StatusCode);

        var mixed = f.As(f.TokenOfSubject("sub-mixed")); // viewer in LE-1, analyst in LE-2
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.SendAsync(Post("/api/v1/exceptions/A5/assign", new { ownerId = "local-approver" }, etag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Analyst.SendAsync(Post("/api/v1/exceptions/NOPE/assign", new { ownerId = "local-approver" }, "\"v1\""))).StatusCode);
        Assert.Equal(0, await Audits("EXCEPTION_ASSIGNED", "A5"));
    }

    [Theory]
    [InlineData("X-DONE", "invalid-state")]
    [InlineData("X-SUP", "run-superseded")]
    public async Task Assign_in_a_closed_state_is_409(string id, string slug)
    {
        var c = Analyst;
        var res = await c.SendAsync(Post($"/api/v1/exceptions/{id}/assign", new { ownerId = "local-approver" }, await EtagOf(c, id)));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.EndsWith("/" + slug, (await Json(res)).GetProperty("type").GetString());
        Assert.Equal(0, await Audits("EXCEPTION_ASSIGNED", id));
    }

    [Theory]
    [InlineData("u-norole")]
    [InlineData("u-off")]
    [InlineData("u-le2")]
    [InlineData("no-such-user")]
    public async Task Assign_to_an_ineligible_owner_is_422_and_changes_nothing(string owner)
    {
        var c = Analyst;
        var res = await c.SendAsync(Post("/api/v1/exceptions/A6/assign", new { ownerId = owner }, await EtagOf(c, "A6")));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Equal(0, await Audits("EXCEPTION_ASSIGNED", "A6"));
        Assert.Null(await f.Scalar<string>("SELECT owner_user_id FROM exceptions WHERE id = 'A6'"));
    }

    [Fact]
    public async Task Assign_with_a_too_short_note_is_422()
    {
        var c = Analyst;
        var res = await c.SendAsync(Post("/api/v1/exceptions/A6/assign", new { ownerId = "local-approver", note = "ok" }, await EtagOf(c, "A6")));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
    }

    [Fact]
    public async Task Two_concurrent_assigns_with_the_same_etag_one_wins_one_gets_412()
    {
        var c = Analyst;
        var etag = await EtagOf(c, "A7");
        var tasks = new[] { "local-approver", "local-analyst" }.Select(o => c.SendAsync(Post("/api/v1/exceptions/A7/assign", new { ownerId = o }, etag))).ToArray();
        var codes = (await Task.WhenAll(tasks)).Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal([200, 412], codes);
        Assert.Equal(1, await Audits("EXCEPTION_ASSIGNED", "A7"));
    }

    [Fact]
    public async Task Note_is_added_to_the_timeline_with_audit_and_moves_the_etag()
    {
        var c = Analyst;
        var etag = await EtagOf(c, "N1");
        var res = await c.SendAsync(Post("/api/v1/exceptions/N1/notes", new { text = "Called the bank." }, etag));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEqual(etag, res.Headers.ETag!.ToString());
        var body = await Json(res);
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("type").GetString() == "NOTE" && t.GetProperty("text").GetString() == "Called the bank.");
        Assert.Equal("OPEN", body.GetProperty("status").GetString());
        Assert.Equal(1, await Audits("NOTE_ADDED", "N1"));
        Assert.Equal(1L, await f.Scalar<long>("SELECT count(*) FROM exception_notes WHERE exception_id = 'N1'"));
    }

    [Fact]
    public async Task Note_validation_permission_and_state()
    {
        var c = Analyst;
        var etag = await EtagOf(c, "N2");
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exceptions/N2/notes", new { text = "   " }, etag))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exceptions/N2/notes", new { text = "" }, etag))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.SendAsync(Post("/api/v1/exceptions/N2/notes", new { text = "hello" }, etag))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Post("/api/v1/exceptions/X-DONE/notes", new { text = "hello" }, await EtagOf(c, "X-DONE")))).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await c.SendAsync(Post("/api/v1/exceptions/N2/notes", new { text = "hello" }, "\"v7\""))).StatusCode);
        Assert.Equal(0, await Audits("NOTE_ADDED", "N2"));
        Assert.Equal(0, await Audits("NOTE_ADDED", "X-DONE"));
    }

    [Fact]
    public async Task Note_is_also_allowed_while_a_resolution_is_pending()
    {
        var c = Analyst;
        var res = await c.SendAsync(Post("/api/v1/exceptions/X-PEND/notes", new { text = "context for the approver" }, await EtagOf(c, "X-PEND")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Request_evidence_moves_to_waiting_for_data_with_timeline_and_audit()
    {
        var c = Analyst;
        var res = await c.SendAsync(Post("/api/v1/exceptions/E1/request-evidence", new { text = "Need the bank statement", requestedFrom = "bank" }, await EtagOf(c, "E1")));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await Json(res);
        Assert.Equal("WAITING_FOR_DATA", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("states").EnumerateArray(), s => s.GetString() == "WAITING_FOR_DATA");
        Assert.Contains(body.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("type").GetString() == "EVIDENCE_REQUEST" && t.GetProperty("text").GetString()!.Contains("bank"));
        Assert.Equal(1, await Audits("EVIDENCE_REQUESTED", "E1"));
        // a second request while already waiting is allowed (stays WAITING_FOR_DATA)
        var again = await c.SendAsync(Post("/api/v1/exceptions/E1/request-evidence", new { text = "And the ledger extract" }, res.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Request_evidence_rejects_pending_approval_closed_missing_etag_and_missing_permission()
    {
        var c = Analyst;
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Post("/api/v1/exceptions/X-PEND/request-evidence", new { text = "more please" }, await EtagOf(c, "X-PEND")))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(Post("/api/v1/exceptions/X-DONE/request-evidence", new { text = "more please" }, await EtagOf(c, "X-DONE")))).StatusCode);
        var etag = await EtagOf(c, "E2");
        Assert.Equal((HttpStatusCode)428, (await c.SendAsync(Post("/api/v1/exceptions/E2/request-evidence", new { text = "more please" }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.SendAsync(Post("/api/v1/exceptions/E2/request-evidence", new { text = "more please" }, etag))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exceptions/E2/request-evidence", new { }, etag))).StatusCode);
        Assert.Equal("OPEN", await f.Scalar<string>("SELECT status FROM exceptions WHERE id = 'E2'"));
        Assert.Equal(0, await Audits("EVIDENCE_REQUESTED", "E2"));
    }

    [Fact]
    public async Task Request_evidence_replay_executes_once()
    {
        var c = Analyst;
        var etag = await EtagOf(c, "E3");
        var key = Guid.NewGuid().ToString();
        var a = await c.SendAsync(Post("/api/v1/exceptions/E3/request-evidence", new { text = "Need statement" }, etag, key));
        var b = await c.SendAsync(Post("/api/v1/exceptions/E3/request-evidence", new { text = "Need statement" }, etag, key));
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.True(b.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(1, await Audits("EVIDENCE_REQUESTED", "E3"));
    }

    // ---- bulk ----

    private static object Items(params (string Id, string Etag)[] items) => items.Select(i => new { exceptionId = i.Id, etag = i.Etag }).ToArray();

    [Fact]
    public async Task Bulk_assign_all_ok_is_200_with_per_item_etag_and_one_audit_each()
    {
        var c = Analyst;
        var items = Items(("B1", await EtagOf(c, "B1")), ("B2", await EtagOf(c, "B2")));
        var res = await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", note = "Batch one", items }, null));
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var results = (await Json(res)).GetProperty("results").EnumerateArray().ToArray();
        Assert.All(results, r => { Assert.Equal(200, r.GetProperty("status").GetInt32()); Assert.StartsWith("\"v", r.GetProperty("etag").GetString()); });
        Assert.Equal(1, await Audits("EXCEPTION_ASSIGNED", "B1"));
        Assert.Equal(1, await Audits("EXCEPTION_ASSIGNED", "B2"));
        Assert.Equal(results[0].GetProperty("etag").GetString(), await EtagOf(c, "B1"));
    }

    [Fact]
    public async Task Bulk_assign_partial_is_207_failures_do_not_roll_back_the_others()
    {
        var c = Analyst;
        var good = await EtagOf(c, "B3");
        var items = Items(("B3", good), ("B4", "\"v42\""), ("X-DONE", await EtagOf(c, "X-DONE")), ("X-OTHER", "\"v1\""), ("MISSING", "\"v1\""));
        var res = await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items }, null));
        Assert.Equal((HttpStatusCode)207, res.StatusCode);
        var results = (await Json(res)).GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("exceptionId").GetString()!);
        Assert.Equal(200, results["B3"].GetProperty("status").GetInt32());
        Assert.Equal(412, results["B4"].GetProperty("status").GetInt32());
        Assert.EndsWith("/precondition-failed", results["B4"].GetProperty("problem").GetProperty("type").GetString());
        Assert.Equal(409, results["X-DONE"].GetProperty("status").GetInt32());
        Assert.Equal(404, results["X-OTHER"].GetProperty("status").GetInt32()); // not in run R1
        Assert.Equal(404, results["MISSING"].GetProperty("status").GetInt32());
        Assert.Equal(1, await Audits("EXCEPTION_ASSIGNED", "B3"));
        Assert.Equal(0, await Audits("EXCEPTION_ASSIGNED", "B4"));
        Assert.Equal("local-approver", await f.Scalar<string>("SELECT owner_user_id FROM exceptions WHERE id = 'B3'"));
        Assert.Null(await f.Scalar<string>("SELECT owner_user_id FROM exceptions WHERE id = 'B4'"));
    }

    [Fact]
    public async Task Bulk_assign_limits_permissions_and_validation()
    {
        var c = Analyst;
        var many = Enumerable.Range(0, 201).Select(i => (Id: "Z" + i, Etag: "\"v1\"")).ToArray();
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items(many) }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items() }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items(("B5", "\"v1\""), ("B5", "\"v1\"")) }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Viewer.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items(("B5", "\"v1\"")) }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As(f.TokenOfSubject("sub-le2")).SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items(("B5", "\"v1\"")) }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Post("/api/v1/runs/NOPE/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items(("B5", "\"v1\"")) }, null))).StatusCode);
        // 200 items is accepted by the limit check (every item fails individually as unknown)
        var ok200 = Enumerable.Range(0, 200).Select(i => (Id: "Q" + i, Etag: "\"v1\"")).ToArray();
        Assert.Equal((HttpStatusCode)207, (await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "local-approver", items = Items(ok200) }, null))).StatusCode);
    }

    [Fact]
    public async Task Bulk_assign_ineligible_owner_fails_each_item_with_422_and_replay_returns_the_stored_207()
    {
        var c = Analyst;
        var items = Items(("B6", await EtagOf(c, "B6")), ("B7", await EtagOf(c, "B7")));
        var key = Guid.NewGuid().ToString();
        var res = await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "u-norole", items }, null, key));
        Assert.Equal((HttpStatusCode)207, res.StatusCode);
        Assert.All((await Json(res)).GetProperty("results").EnumerateArray(), r => Assert.Equal(422, r.GetProperty("status").GetInt32()));
        var replay = await c.SendAsync(Post("/api/v1/runs/R1/exceptions/bulk-assign", new { ownerId = "u-norole", items }, null, key));
        Assert.Equal((HttpStatusCode)207, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));
    }

    // ---- saved views ----

    private static HttpRequestMessage Del(string url, string? etag)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        if (etag is not null) { req.Headers.TryAddWithoutValidation("If-Match", etag); }
        return req;
    }

    [Fact]
    public async Task Saved_view_create_list_use_and_delete()
    {
        var c = f.As(await f.ScopedToken("viewer1", Role.Viewer, "*"));
        var res = await c.SendAsync(Post("/api/v1/exception-views", new { name = "Blocking GBP", filters = new { blocking = true, currency = "GBP" }, sort = "-severity" }, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var view = await Json(res);
        Assert.False(view.GetProperty("shared").GetBoolean());
        Assert.Equal("u-viewer1", view.GetProperty("owner").GetProperty("id").GetString());
        var id = view.GetProperty("id").GetString()!;

        var list = await Json(await c.GetAsync("/api/v1/exception-views"));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), v => v.GetProperty("id").GetString() == id && v.GetProperty("sort").GetString() == "-severity");

        var used = await c.GetAsync($"/api/v1/runs/R1/exceptions?savedViewId={id}");
        Assert.Equal(HttpStatusCode.OK, used.StatusCode);

        var etag = res.Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Del("/api/v1/exception-views/NOPE", etag))).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await c.SendAsync(Del("/api/v1/exception-views/" + id, null))).StatusCode);
        Assert.Equal((HttpStatusCode)412, (await c.SendAsync(Del("/api/v1/exception-views/" + id, "\"v9\""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Del("/api/v1/exception-views/" + id, etag))).StatusCode);
        Assert.Equal(0L, await f.Scalar<long>("SELECT count(*) FROM saved_views WHERE id = @i", ("i", id)));
    }

    [Fact]
    public async Task Saved_view_rejects_shared_unknown_filters_bad_values_duplicates_and_other_users_views_are_hidden()
    {
        var c = f.As(await f.ScopedToken("viewer2", Role.Viewer, "*"));
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exception-views", new { name = "S", shared = true, filters = new { } }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exception-views", new { name = "U", filters = new { nonsense = 1 } }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exception-views", new { name = "V", filters = new { severity = new[] { "BOGUS" } } }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exception-views", new { name = "W", filters = new { }, sort = "bogus" }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exception-views", new { name = " ", filters = new { } }, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Post("/api/v1/exception-views", new { name = "Same", filters = new { } }, null))).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await c.SendAsync(Post("/api/v1/exception-views", new { name = "Same", filters = new { } }, null))).StatusCode);

        var other = f.As(await f.ScopedToken("viewer3", Role.Viewer, "*"));
        var otherList = await Json(await other.GetAsync("/api/v1/exception-views"));
        Assert.DoesNotContain(otherList.GetProperty("items").EnumerateArray(), v => v.GetProperty("name").GetString() == "Same");
    }

    [Fact]
    public async Task Saved_view_limit_is_50_per_user_and_delete_of_a_shared_view_of_someone_else_is_403()
    {
        var c = f.As(await f.ScopedToken("viewer4", Role.Viewer, "*"));
        for (var i = 0; i < 50; i++)
        {
            var r = await c.SendAsync(Post("/api/v1/exception-views", new { name = "v" + i, filters = new { } }, null));
            Assert.True(r.StatusCode == HttpStatusCode.Created, i + ": " + await r.Content.ReadAsStringAsync());
        }
        var over = await c.SendAsync(Post("/api/v1/exception-views", new { name = "one too many", filters = new { } }, null));
        Assert.Equal((HttpStatusCode)422, over.StatusCode);
        Assert.Equal(50L, await f.Scalar<long>("SELECT count(*) FROM saved_views WHERE user_id = 'u-viewer4'"));

        await f.Exec("INSERT INTO saved_views (id, user_id, scope, name, filters, shared) VALUES ('SV-SHARED','u-viewer1','exceptions','team','{}',true), ('SV-PRIVATE','u-viewer1','exceptions','mine','{}',false)");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Del("/api/v1/exception-views/SV-SHARED", "\"v1\""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Del("/api/v1/exception-views/SV-PRIVATE", "\"v1\""))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_view_creation_never_exceeds_the_cap()
    {
        var c = f.As(await f.ScopedToken("viewer5", Role.Viewer, "*"));
        await f.Exec("INSERT INTO saved_views (id, user_id, scope, name, filters) SELECT 'SV5-' || g, 'u-viewer5', 'exceptions', 'n' || g, '{}' FROM generate_series(1, 48) g");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => c.SendAsync(Post("/api/v1/exception-views", new { name = "race" + i, filters = new { } }, null))));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(50L, await f.Scalar<long>("SELECT count(*) FROM saved_views WHERE user_id = 'u-viewer5'"));
    }
}
