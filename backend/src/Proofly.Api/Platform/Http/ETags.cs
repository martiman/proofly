using System.Security.Cryptography;
using System.Text;
using Proofly.Api.Platform.Problems;

namespace Proofly.Api.Platform.Http;

/// <summary>ETag formatting and If-Match / If-None-Match evaluation (RFC 9110).</summary>
public static class ETags
{
    /// <summary>Strong validator: the exact representation/version. Required for If-Match (strong comparison).</summary>
    public static string Strong(string opaque) => Quote(opaque, weak: false);
    /// <summary>Weak validator. Never satisfies If-Match; usable for If-None-Match (304) only.</summary>
    public static string Weak(string opaque) => Quote(opaque, weak: true);
    public static string ForVersion(long version) => Strong("v" + version);
    public static string ForBytes(ReadOnlySpan<byte> content) => Strong(Convert.ToHexString(SHA256.HashData(content))[..32].ToLowerInvariant());
    public static string ForText(string content) => ForBytes(Encoding.UTF8.GetBytes(content));

    private static string Quote(string opaque, bool weak)
    {
        if (opaque.Length == 0 || opaque.Any(c => c is '"' or '\\' || c < 0x21 || c > 0x7e)) { throw new ArgumentException("ETag value must be printable ASCII without quotes.", nameof(opaque)); }
        return (weak ? "W/\"" : "\"") + opaque + "\"";
    }

    public static void Set(this HttpContext ctx, string etag) => ctx.Response.Headers.ETag = etag;

    public static bool IsWeak(string etag) => etag.StartsWith("W/", StringComparison.Ordinal);

    /// <summary>Strong comparison: both strong and byte-identical.</summary>
    public static bool StrongMatch(string a, string b) => !IsWeak(a) && !IsWeak(b) && a == b;

    /// <summary>Weak comparison: opaque tags equal regardless of the W/ prefix.</summary>
    public static bool WeakMatch(string a, string b) => Strip(a) == Strip(b);

    private static string Strip(string e) => IsWeak(e) ? e[2..] : e;

    internal static IReadOnlyList<string> ParseList(string header) =>
        header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Enforces If-Match for a mutation of an existing resource: missing header -> 428, no strong match -> 412.
    /// Call after loading the resource, before changing it. Idempotent replays never reach the handler, so a replayed command is unaffected.
    /// </summary>
    public static void RequireIfMatch(this HttpContext ctx, string currentETag)
    {
        var header = ctx.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header)) { throw new ProblemException(ProblemTypes.PreconditionRequired, "Send the ETag of the resource as last read in If-Match."); }
        var candidates = ParseList(header);
        if (candidates.Contains("*") || candidates.Any(c => StrongMatch(c, currentETag))) { return; }
        throw new ProblemException(ProblemTypes.PreconditionFailed, "The resource changed since it was read; re-read it and retry.");
    }

    /// <summary>For GET: true if If-None-Match matches (weak comparison). Respond 304 and skip the body.</summary>
    public static bool IsNotModified(this HttpContext ctx, string currentETag)
    {
        var header = ctx.Request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrWhiteSpace(header)) { return false; }
        var candidates = ParseList(header);
        return candidates.Contains("*") || candidates.Any(c => WeakMatch(c, currentETag));
    }
}
