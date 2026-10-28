namespace Proofly.Data.Exceptions;

public enum SortKeyType { Int, Timestamp, Date, Numeric, Text }

/// <summary>One sortable column: a never-null SQL expression, its cast type and whether the wire direction is inverted (ageDays).</summary>
internal sealed record SortColumn(string Sql, SortKeyType Type, bool Inverted = false);

public sealed record SortKey(string Name, bool Descending);

/// <summary>Parsed <c>sort</c> parameter. The id key is always appended last so ordering is total and keyset paging is stable.</summary>
public sealed class ExceptionSortSpec
{
    public const string Default = "-severity,dueAt,id";

    internal static readonly IReadOnlyDictionary<string, SortColumn> Columns = new Dictionary<string, SortColumn>
    {
        ["severity"] = new("CASE e.severity WHEN 'CRITICAL' THEN 4 WHEN 'HIGH' THEN 3 WHEN 'MEDIUM' THEN 2 ELSE 1 END", SortKeyType.Int),
        ["dueAt"] = new("e.due_at", SortKeyType.Timestamp),
        ["createdAt"] = new("e.created_at", SortKeyType.Timestamp),
        ["ageDays"] = new("e.created_at", SortKeyType.Timestamp, Inverted: true),
        ["businessDate"] = new("e.business_date", SortKeyType.Date),
        ["status"] = new("(e.status COLLATE \"C\")", SortKeyType.Text),
        ["control"] = new("(e.control COLLATE \"C\")", SortKeyType.Text),
        ["value"] = new("COALESCE(abs(e.value_amount), -1)", SortKeyType.Numeric), // absolute value; exceptions without value sort lowest
        ["id"] = new("(e.id COLLATE \"C\")", SortKeyType.Text),
    };

    public IReadOnlyList<SortKey> Keys { get; }
    public string Normalized => string.Join(',', Keys.Select(k => (k.Descending ? "-" : "") + k.Name));

    private ExceptionSortSpec(IReadOnlyList<SortKey> keys) => Keys = keys;

    public static bool TryParse(string? raw, out ExceptionSortSpec spec, out string? error)
    {
        spec = null!;
        error = null;
        var keys = new List<SortKey>();
        foreach (var part in (string.IsNullOrWhiteSpace(raw) ? Default : raw).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var desc = part.StartsWith('-');
            var name = desc ? part[1..] : part;
            if (!Columns.ContainsKey(name)) { error = $"Unknown sort key '{name}'. Allowed: {string.Join(", ", Columns.Keys)}."; return false; }
            if (keys.Any(k => k.Name == name)) { error = $"Sort key '{name}' is repeated."; return false; }
            keys.Add(new SortKey(name, desc));
        }
        if (keys.Count == 0) { error = "sort is empty."; return false; }
        if (keys.All(k => k.Name != "id")) { keys.Add(new SortKey("id", false)); }
        spec = new ExceptionSortSpec(keys);
        return true;
    }
}
