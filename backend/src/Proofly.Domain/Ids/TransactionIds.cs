using System.Globalization;

namespace Proofly.Domain.Ids;

/// <summary>
/// Public transaction ids: "{prefix}-{pk}" where pk is the bigint identity of the canonical record and the prefix names its table.
/// One scheme for every endpoint (transactions, matching, ledger entries, lineage). See docs/integrations/api/README.md, Identifiers.
/// </summary>
public static class TransactionIds
{
    private static readonly (string Prefix, string Table)[] Map =
    [
        ("TRF", "transfer_instructions"), ("DD", "direct_debit_events"), ("CRD", "card_events"), ("BNK", "bank_transactions"), ("LED", "ledger_entries"),
    ];

    /// <summary>The record tables that hold transactions.</summary>
    public static IReadOnlyList<string> Tables { get; } = Map.Select(m => m.Table).ToList();

    public static string Format(string table, long pk) =>
        Map.FirstOrDefault(m => m.Table == table) is { Prefix: { } p } ? $"{p}-{pk.ToString(CultureInfo.InvariantCulture)}"
            : throw new ArgumentException($"'{table}' is not a transaction table.", nameof(table));

    public static bool TryParse(string? id, out string table, out long pk)
    {
        table = ""; pk = 0;
        if (id is null) { return false; }
        var dash = id.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !long.TryParse(id.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out pk)) { return false; }
        var m = Map.FirstOrDefault(x => x.Prefix == id[..dash]);
        if (m.Table is null) { return false; }
        table = m.Table;
        return true;
    }
}
