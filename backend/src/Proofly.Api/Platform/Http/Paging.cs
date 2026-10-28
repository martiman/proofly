using System.Text.Json;
using Proofly.Api.Platform.Json;
using Proofly.Api.Platform.Problems;
using System.Text.Json.Serialization;

namespace Proofly.Api.Platform.Http;

/// <summary>Response envelope: { items, page: { nextCursor, hasMore, total? } }.</summary>
/// <summary>page object: nextCursor is always present (null on the last page); total only when includeTotal=true.</summary>
public sealed record PageInfo(string? NextCursor, bool HasMore, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, PageInfo Page);

public sealed record PageRequest(int Limit, string? Cursor, bool IncludeTotal)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 200;

    /// <summary>Reads limit/cursor/includeTotal. The contract validator already enforced the ranges; this guards non-contract use.</summary>
    public static PageRequest From(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var limit = DefaultLimit;
        if (q.TryGetValue("limit", out var l) && (!int.TryParse(l, out limit) || limit is < 1 or > MaxLimit))
        {
            throw ProblemException.Validation("limit", $"limit must be an integer from 1 to {MaxLimit}.");
        }
        var includeTotal = q.TryGetValue("includeTotal", out var t) && bool.TryParse(t, out var b) && b;
        return new PageRequest(limit, q.TryGetValue("cursor", out var c) && c.Count > 0 && c[0] is { Length: > 0 } cs ? cs : null, includeTotal);
    }

    /// <summary>Decodes the opaque cursor into the caller's keyset state; null when no cursor was sent. Invalid -> 422 on "cursor".</summary>
    public TState? Decode<TState>() where TState : class => Cursors.Decode<TState>(Cursor);
}

public static class Cursors
{
    /// <summary>Opaque cursor: base64url of the JSON keyset state. Clients must not parse it.</summary>
    public static string Encode<TState>(TState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, state!.GetType(), WireJson.Options);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static TState? Decode<TState>(string? cursor) where TState : class
    {
        if (string.IsNullOrEmpty(cursor)) { return null; }
        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<TState>(Convert.FromBase64String(s), WireJson.Options)
                ?? throw new FormatException("empty cursor");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw ProblemException.Validation("cursor", "The cursor is not valid.");
        }
    }
}

public static class Paging
{
    /// <summary>
    /// Build a page from a keyset query that fetched up to Limit + 1 rows in the endpoint's stable sort.
    /// <paramref name="cursorState"/> maps the last returned item to the keyset state encoded in nextCursor.
    /// </summary>
    public static PagedResult<T> Create<T>(IReadOnlyList<T> fetched, PageRequest request, Func<T, object> cursorState, int? total = null)
    {
        var hasMore = fetched.Count > request.Limit;
        var items = hasMore ? fetched.Take(request.Limit).ToList() : fetched;
        var next = hasMore ? Cursors.Encode(cursorState(items[^1])) : null;
        return new PagedResult<T>(items, new PageInfo(next, hasMore, request.IncludeTotal ? total : null));
    }
}
