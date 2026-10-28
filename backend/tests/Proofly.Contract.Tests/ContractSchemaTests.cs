using System.Net;
using System.Text.Json.Nodes;

namespace Proofly.Contract.Tests;

/// <summary>Responses of the safe GET operations, with seeded data, validate against the contract schema.</summary>
[Collection(ContractApiCollection.Name)]
public class ContractSchemaTests(ContractApiFixture f)
{
    // Placeholder ids that exist in Seed.Sql.
    private static readonly Dictionary<string, string> Ids = new()
    {
        ["runId"] = "R1", ["deliveryId"] = "D1", ["exceptionId"] = "X-1", ["transactionId"] = "TRF-1",
        ["accountId"] = "ACC-1", ["groupId"] = "MG-1", ["recordId"] = "transfer_instructions:1", ["changeId"] = "CC-1", ["subjectKind"] = "EXCEPTION", ["subjectId"] = "X-1", ["currency"] = "GBP", ["legalEntityId"] = "LE-A",
    };

    private static readonly Dictionary<string, string> Query = new() { ["compareRuns"] = "against=R0", ["listSafeguardingLines"] = "kind=obligation" };

    private static HashSet<string> Unimplemented() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "unimplemented-operations.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<Op> SafeGets() => ContractDoc.Ops.Where(o => o.Method == "GET" && o.Spec.Responses.TryGetValue("200", out var r) && r.SchemaPointer is not null);

    public static IEnumerable<object[]> Gets() => SafeGets().Select(o => new object[] { o.OperationId });

    [Theory]
    [MemberData(nameof(Gets))]
    public async Task Response_matches_the_contract_schema(string operationId)
    {
        var op = ContractDoc.ById(operationId);
        if (Unimplemented().Contains(operationId)) { return; }

        var res = await (operationId == "getMe" ? f.RealViewer() : f.Everything()).SendAsync(Requests.Build(op, pathValues: Ids, query: Query.GetValueOrDefault(operationId) ?? Requests.RequiredQuery(op)));
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{operationId}: expected 200 with the seeded data, got {(int)res.StatusCode}: {text}");
        Assert.StartsWith("application/json", res.Content.Headers.ContentType?.MediaType);

        var errors = op.Spec.Document.Validate(op.Spec.Responses["200"].SchemaPointer!, JsonNode.Parse(text));
        Assert.True(errors.Count == 0, $"{operationId}: " + string.Join("; ", errors.Select(e => $"{e.Pointer} {e.Message}")));
    }

    [Fact]
    public async Task Paged_lists_return_a_cursor_that_the_next_request_accepts()
    {
        var op = ContractDoc.ById("listExceptions");
        var client = f.Everything();
        var first = JsonNode.Parse(await (await client.SendAsync(Requests.Build(op, pathValues: Ids, query: "limit=1"))).Content.ReadAsStringAsync())!;
        Assert.True((bool)first["page"]!["hasMore"]!);
        var cursor = (string)first["page"]!["nextCursor"]!;
        var second = await client.SendAsync(Requests.Build(op, pathValues: Ids, query: $"limit=1&cursor={Uri.EscapeDataString(cursor)}"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
