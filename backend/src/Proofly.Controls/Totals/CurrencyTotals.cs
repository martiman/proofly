using Proofly.Domain;

namespace Proofly.Controls.Totals;

/// <summary>Pure, deterministic: one total per currency, ordered by currency code.</summary>
public static class CurrencyTotals
{
    public static IReadOnlyList<Money> Sum(IEnumerable<Money> amounts) =>
        amounts.GroupBy(m => m.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Money(g.Sum(m => m.Amount), g.Key))
            .ToList();
}
