using System.Text;

namespace Proofly.Contract.Tests;

internal static class Requests
{
    /// <summary>A syntactically valid request for the operation: placeholder ids, required headers and an empty JSON body when one is declared.</summary>
    public static HttpRequestMessage Build(Op op, bool ifMatch = true, bool idempotencyKey = true, string? query = null, IReadOnlyDictionary<string, string>? pathValues = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(op.Method), op.FilledPath(pathValues) + (query is null ? "" : "?" + query));
        if (op.Spec.RequiresIfMatch && ifMatch) { req.Headers.TryAddWithoutValidation("If-Match", "\"1\""); }
        if (op.Spec.RequiresIdempotencyKey && idempotencyKey) { req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); }
        if (op.Spec.BodyDeclared) { req.Content = new StringContent("{}", Encoding.UTF8, "application/json"); }
        return req;
    }

    public static string RequiredQuery(Op op) =>
        string.Join('&', op.Spec.Parameters.Where(p => p.In == "query" && p.Required).Select(p => p.Name + "=x"));
}
