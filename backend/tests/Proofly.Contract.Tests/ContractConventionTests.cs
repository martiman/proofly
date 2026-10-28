using System.Net;
using System.Text.Json.Nodes;

namespace Proofly.Contract.Tests;

/// <summary>Cross-cutting API conventions stated by the contract (README, Conventions) hold for every operation.</summary>
[Collection(ContractApiCollection.Name)]
public class ContractConventionTests(ContractApiFixture f)
{
    private static HashSet<string> Unimplemented() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "unimplemented-operations.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<object[]> Ops() => ContractDoc.OpData();

    // Personal saved filters are not a material decision, so the contract gives them no audit event.
    private static readonly HashSet<string> MutationsWithoutAudit = ["createExceptionView", "deleteExceptionView"];

    [Fact]
    public void Every_mutation_requires_If_Match_or_Idempotency_Key_and_material_ones_name_an_audit_event()
    {
        foreach (var op in ContractDoc.Ops.Where(o => o.IsMutation))
        {
            Assert.True(op.RequiredHeaders.Contains("If-Match") || op.RequiredHeaders.Contains("Idempotency-Key"),
                $"{op.OperationId}: a mutation must require If-Match or Idempotency-Key.");
            if (op.Method == "POST") { Assert.True(op.RequiredHeaders.Contains("Idempotency-Key"), $"{op.OperationId}: POST must require Idempotency-Key."); }
            if (!MutationsWithoutAudit.Contains(op.OperationId)) { Assert.True(op.AuditEvent is not null, $"{op.OperationId}: no x-audit-event."); }
            else { Assert.Null(op.AuditEvent); }
        }
        foreach (var o in ContractDoc.Ops.Where(o => !o.IsMutation)) { Assert.True(o.RequiredHeaders.Count == 0, $"{o.OperationId}: a read declares a precondition header."); }
    }

    [Fact]
    public void Every_paged_list_declares_limit_and_cursor_and_returns_a_PageInfo()
    {
        foreach (var op in ContractDoc.Ops)
        {
            var names = op.Spec.Parameters.Where(p => p.In == "query").Select(p => p.Name).ToHashSet();
            var paged = op.SuccessSchemaRef?.EndsWith("Page", StringComparison.Ordinal) == true;
            if (paged)
            {
                Assert.True(names.Contains("limit") && names.Contains("cursor"), $"{op.OperationId} returns a page but lacks limit/cursor.");
                var schema = ContractDoc.Root["components"]!["schemas"]![op.SuccessSchemaRef!]!;
                Assert.Contains("page", ((JsonObject)schema["properties"]!).Select(kv => kv.Key));
                Assert.Contains("PageInfo", (string)schema["properties"]!["page"]!["$ref"]!);
            }
            else if (names.Contains("limit")) { Assert.Contains("cursor", names); }
        }
        Assert.Contains(ContractDoc.Ops, o => o.SuccessSchemaRef?.EndsWith("Page", StringComparison.Ordinal) == true);
    }

    private static readonly HashSet<string> Missing428 = ["deleteExceptionView"];

    [Fact]
    public void Every_operation_documents_the_401_and_mutations_with_preconditions_document_412_and_428()
    {
        foreach (var op in ContractDoc.Ops)
        {
            Assert.True(op.Spec.Responses.ContainsKey("401"), $"{op.OperationId} does not declare 401.");
            if (op.Spec.RequiresIfMatch)
            {
                Assert.True(op.Spec.Responses.ContainsKey("412"), $"{op.OperationId} takes If-Match but does not declare 412.");
                // Contract gap, to be fixed in openapi.yaml: the API answers 428 here but the response is not declared. The list must shrink.
                var declares428 = op.Spec.Responses.ContainsKey("428");
                Assert.Equal(Missing428.Contains(op.OperationId), !declares428);
            }
        }
    }

    private async Task AssertProblem(HttpResponseMessage res, HttpStatusCode status, Op op)
    {
        Assert.Equal(status, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(res.Headers.GetValues("X-Request-Id").Single(), (string?)body["requestId"]);
        Assert.Equal((int)status, (int?)body["status"]);
        var errors = op.Spec.Document.Validate("/components/schemas/Problem", body);
        Assert.True(errors.Count == 0, $"{op.OperationId} {(int)status}: " + string.Join("; ", errors.Select(e => e.Pointer + " " + e.Message)));
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task Error_bodies_are_problem_json_with_request_id_and_commands_enforce_preconditions(string operationId)
    {
        var op = ContractDoc.ById(operationId);
        if (Unimplemented().Contains(operationId)) { return; }

        // 401
        await AssertProblem(await f.Client(null).SendAsync(Requests.Build(op)), HttpStatusCode.Unauthorized, op);

        // 403 (permission), unless the operation is open to every authenticated caller
        if (op.Permission != "auth.self")
        {
            var others = ContractApiFixture.AllPermissions.Where(p => p != op.Permission);
            await AssertProblem(await f.Client("lacking", others).SendAsync(Requests.Build(op)), HttpStatusCode.Forbidden, op);
        }

        var all = f.Everything();
        // 422: an undeclared query parameter is rejected, never ignored.
        await AssertProblem(await all.SendAsync(Requests.Build(op, query: "undeclared-parameter=1")), HttpStatusCode.UnprocessableEntity, op);

        // 428: a missing If-Match.
        if (op.Spec.RequiresIfMatch)
        {
            await AssertProblem(await all.SendAsync(Requests.Build(op, ifMatch: false, query: Requests.RequiredQuery(op))), HttpStatusCode.PreconditionRequired, op);
        }

        // Idempotency-Key is required (422 with a field error naming the header).
        if (op.Spec.RequiresIdempotencyKey)
        {
            var res = await all.SendAsync(Requests.Build(op, idempotencyKey: false));
            await AssertProblem(res, HttpStatusCode.UnprocessableEntity, op);
            Assert.Contains("Idempotency-Key", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task An_unrouted_path_is_a_problem_json_404_with_request_id()
    {
        var res = await f.Everything().GetAsync("/api/v1/not-in-the-contract");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.NotNull((string?)JsonNode.Parse(await res.Content.ReadAsStringAsync())!["requestId"]);
    }
}
