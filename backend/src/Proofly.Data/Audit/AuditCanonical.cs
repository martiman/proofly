using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Proofly.Data.Audit;

/// <summary>
/// Canonical serialisation and hashing. The hashed form is a JSON array of the fields in a fixed order, with
/// before/after re-serialised with object keys sorted ordinally and no whitespace, so key order and formatting of the
/// input never change the hash. Timestamps are UTC with microsecond precision (the PostgreSQL resolution).
/// </summary>
public static class AuditCanonical
{
    public static string CanonicalJson(string? json)
    {
        if (json is null) return "null";
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        return Write(node);
    }

    private static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        Append(sb, node);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    Append(sb, kv.Value);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Append(sb, a[i]);
                }
                sb.Append(']');
                break;
            default:
                var raw = node.ToJsonString();
                if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
                    && (raw.Contains('e') || raw.Contains('E')))
                    throw new ArgumentException("Audit JSON must not contain exponent numbers; use decimal strings.");
                sb.Append(raw);
                break;
        }
    }

    public static DateTimeOffset TruncateToMicros(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);

    public static string Timestamp(DateTimeOffset t) =>
        TruncateToMicros(t).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Canonical string that is hashed for an event.</summary>
    public static string Canonicalize(
        string chainId, long chainSeq, DateTimeOffset at, string type, string actorType, string? actorId,
        string? actorName, string subjectKind, string subjectId, string? runId, string? ruleVersion, string? reason,
        string summary, string? requestId, string? beforeJson, string? afterJson, string? previousHash)
    {
        var sb = new StringBuilder("[");
        void S(string? s) { sb.Append(s is null ? "null" : JsonSerializer.Serialize(s)).Append(','); }
        S(chainId);
        sb.Append(chainSeq.ToString(CultureInfo.InvariantCulture)).Append(',');
        S(Timestamp(at)); S(type); S(actorType); S(actorId); S(actorName); S(subjectKind); S(subjectId);
        S(runId); S(ruleVersion); S(reason); S(summary); S(requestId);
        sb.Append(CanonicalJson(beforeJson)).Append(',').Append(CanonicalJson(afterJson)).Append(',');
        sb.Append(previousHash is null ? "null" : JsonSerializer.Serialize(previousHash)).Append(']');
        return sb.ToString();
    }

    public static string Sha256Hex(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
