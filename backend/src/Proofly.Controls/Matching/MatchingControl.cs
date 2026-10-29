using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Matching;

/// <summary>
/// Pure, deterministic matching engine (docs/controls/matching.md). Rules run in a fixed order over each pair:
/// exact identifier, exact composite, date tolerance, amount tolerance, controlled grouped, then value-mismatch and
/// currency-mismatch diagnosis. A record is committed to at most one group per pair (no reuse). Whenever a rule
/// finds several equally valid candidates, nothing is matched: the whole candidate cluster is reported as
/// AMBIGUOUS_CANDIDATES and removed from lower-priority rules, so a looser rule can never silently resolve it.
/// Currencies are never netted: every rule requires equal currency (except the diagnostic CURRENCY_MISMATCH).
/// </summary>
public static class MatchingControl
{
    public static MatchingResult Evaluate(MatchingInput input, MatchingOptions options)
    {
        var groups = new List<MatchGroup>();
        foreach (var pair in input.Pairs.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            new PairRun(pair, options, groups).Run();
        }

        var ordered = CollapseExternalLeftovers(input, groups).OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        var findings = ordered.Select(g => ToFinding(g, options)).OfType<Finding>();
        return new MatchingResult(ControlResult.From(ControlArea.Matching, options.RuleVersion, findings), ordered);
    }

    /// <summary>
    /// A bank (external) record sits on the right of several pairs. It is unmatched only if it is unmatched in every pair it could
    /// take part in: a 0:1 NO_CANDIDATE group for it is dropped when any pair matched or already diagnosed it, and otherwise reported once (in the first
    /// pair by ordinal name) with the pairs considered named in the rationale. A value mismatch against a bank record is reported once, in the first pair
    /// that found it. Internal records are judged in their own pair.
    /// </summary>
    private static List<MatchGroup> CollapseExternalLeftovers(MatchingInput input, List<MatchGroup> groups)
    {
        // A bank record already diagnosed in some pair (matched, value mismatch, ambiguous, currency mismatch) is not also "unmatched everywhere".
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups.Where(g => g.Status == MatchGroupStatus.Matched || !(g.ReasonCode == MatchingCodes.NoCandidate && g.Left.Count == 0)))
        {
            foreach (var r in g.Right.Where(r => r.SourceName == SourceNames.BankingTransactions))
            {
                matched.Add(r.SourceName + "/" + r.SourceRowId);
            }
        }
        var pairsByKey = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var p in input.Pairs)
        {
            foreach (var r in p.Right.Where(r => r.Source == SourceNames.BankingTransactions))
            {
                (pairsByKey.TryGetValue(r.Key, out var set) ? set : pairsByKey[r.Key] = new SortedSet<string>(StringComparer.Ordinal)).Add(p.Name);
            }
        }
        var result = new List<MatchGroup>(groups.Count);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var mismatched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups.OrderBy(g => g.Pair, StringComparer.Ordinal).ThenBy(g => g.Id, StringComparer.Ordinal))
        {
            // One bank movement, one value mismatch: when several pairs (instruction, ledger, direct debit) disagree with the same bank record
            // by the amount, it is reported once, in the first pair by ordinal name; the other pairs' groups for it are not repeated.
            if (g.Status == MatchGroupStatus.ValueMismatch)
            {
                var bankKeys = g.Right.Where(r => r.SourceName == SourceNames.BankingTransactions).Select(r => r.SourceName + "/" + r.SourceRowId).ToList();
                if (bankKeys.Count > 0)
                {
                    if (bankKeys.Any(mismatched.Contains)) continue;
                    foreach (var k in bankKeys) mismatched.Add(k);
                }
            }
            var isLeftover = g.ReasonCode == MatchingCodes.NoCandidate && g.Left.Count == 0 && g.Right.Count == 1
                && g.Right[0].SourceName == SourceNames.BankingTransactions;
            if (!isLeftover)
            {
                result.Add(g);
                continue;
            }
            var key = g.Right[0].SourceName + "/" + g.Right[0].SourceRowId;
            if (matched.Contains(key) || !reported.Add(key))
            {
                continue;
            }
            var pairs = pairsByKey.TryGetValue(key, out var ps) ? ps : [];
            result.Add(pairs.Count > 1
                ? g with { Rationale = g.Rationale + " Unmatched in every pair considered: " + string.Join(", ", pairs) + "." }
                : g);
        }
        return result;
    }

    private static Finding? ToFinding(MatchGroup g, MatchingOptions o)
    {
        if (g.Status == MatchGroupStatus.Matched)
        {
            return null;
        }
        var severity = g.ReasonCode switch
        {
            FindingCodes.ValueMismatch or MatchingCodes.CurrencyMismatch => o.ValueMismatchSeverity,
            MatchingCodes.AmbiguousCandidates => o.AmbiguousSeverity,
            _ => o.UnmatchedSeverity,
        };
        var refs = g.Left.Concat(g.Right).Concat(g.Candidates).Distinct()
            .OrderBy(r => r.SourceName, StringComparer.Ordinal).ThenBy(r => r.SourceRowId, StringComparer.Ordinal).ToList();
        return new Finding($"matching:{g.ReasonCode}:{g.Id}", ControlArea.Matching, g.ReasonCode, severity,
            g.Rationale, refs, g.ValueDifference, o.RuleVersion, false, g.Right.Concat(g.Left).Concat(g.Candidates).FirstOrDefault());
    }

    private sealed class PairRun(MatchPair pair, MatchingOptions o, List<MatchGroup> output)
    {
        private readonly List<MatchItem> _left = Sorted(pair.Left);
        private readonly List<MatchItem> _right = Sorted(pair.Right);

        // Records are removed lazily (a set of keys) and the lists are compacted once at the start of each rule, so committing a
        // group is O(group) instead of a scan of the whole pair. Order is preserved, so results are identical.
        private readonly HashSet<string> _removedLeft = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removedRight = new(StringComparer.Ordinal);

        private void Compact()
        {
            if (_removedLeft.Count > 0) { _left.RemoveAll(i => _removedLeft.Contains(i.Key)); _removedLeft.Clear(); }
            if (_removedRight.Count > 0) { _right.RemoveAll(i => _removedRight.Contains(i.Key)); _removedRight.Clear(); }
        }

        private static List<MatchItem> Sorted(IEnumerable<MatchItem> items) =>
            items.OrderBy(i => i.Source, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();

        private static bool Blank(string? s) => string.IsNullOrEmpty(s);
        private static bool RefEq(MatchItem l, MatchItem r) => !Blank(l.Reference) && string.Equals(l.Reference, r.Reference, StringComparison.Ordinal);
        private static bool SameCcy(MatchItem l, MatchItem r) => l.Amount.Currency == r.Amount.Currency;
        private static int? DateDiff(MatchItem l, MatchItem r) =>
            l.Date is { } a && r.Date is { } b ? Math.Abs(a.DayNumber - b.DayNumber) : null;
        private bool DateOk(MatchItem l, MatchItem r) => DateDiff(l, r) is not { } d || d <= o.WindowFor(l.Amount.Currency);
        private bool InTolerance(MatchItem l, MatchItem r) => Math.Abs(l.Amount.Amount - r.Amount.Amount) <= o.ToleranceFor(l.Amount.Currency);

        public void Run()
        {
            Step(MatchingCodes.ExactIdentifierMatch, MatchGroupStatus.Matched, true, false, EdgeIndex.Reference,
                (l, r) => RefEq(l, r) && SameCcy(l, r) && l.Amount.Amount == r.Amount.Amount && DateDiff(l, r) is null or 0);
            Step(MatchingCodes.ExactCompositeMatch, MatchGroupStatus.Matched, true, false, EdgeIndex.CurrencyAmountDate,
                (l, r) => SameCcy(l, r) && l.Amount.Amount == r.Amount.Amount && DateDiff(l, r) == 0 && (Blank(l.Reference) || Blank(r.Reference)));
            Step(MatchingCodes.DateToleranceMatch, MatchGroupStatus.Matched, true, false, EdgeIndex.Reference,
                (l, r) => RefEq(l, r) && SameCcy(l, r) && l.Amount.Amount == r.Amount.Amount && DateOk(l, r));
            Step(MatchingCodes.AmountToleranceMatch, MatchGroupStatus.Matched, true, true, EdgeIndex.Reference,
                (l, r) => RefEq(l, r) && SameCcy(l, r) && InTolerance(l, r) && DateOk(l, r));
            if (o.AllowGroupedMatches)
            {
                Grouped();
            }
            Step(FindingCodes.ValueMismatch, MatchGroupStatus.ValueMismatch, false, true, EdgeIndex.Reference,
                (l, r) => RefEq(l, r) && SameCcy(l, r) && !InTolerance(l, r) && DateOk(l, r));
            Step(MatchingCodes.CurrencyMismatch, MatchGroupStatus.Unmatched, false, false, EdgeIndex.Reference,
                (l, r) => RefEq(l, r) && !SameCcy(l, r));
            Leftovers();
        }

        /// <summary>
        /// Which right-hand records a left record can possibly form an edge with. The index only narrows the candidates: every rule's
        /// own predicate is still applied to each candidate, and candidates are visited in right-list order, so the edges (and their
        /// order) are exactly those of the full cross product, without visiting pairs that cannot match.
        /// </summary>
        private enum EdgeIndex { Reference, CurrencyAmountDate }

        private static readonly IReadOnlyList<MatchItem> NoItems = [];

        private void Step(string ruleId, MatchGroupStatus status, bool tolerant, bool amountTolerant, EdgeIndex index, Func<MatchItem, MatchItem, bool> edge)
        {
            Compact();
            var edges = new List<(MatchItem L, MatchItem R)>();
            // Reference rules require a non-blank equal reference; the composite rule requires equal currency, amount and a known equal date.
            var byReference = new Dictionary<string, List<MatchItem>>(StringComparer.Ordinal);
            var byComposite = new Dictionary<(string, decimal, DateOnly), List<MatchItem>>();
            foreach (var r in _right)
            {
                if (index == EdgeIndex.Reference)
                {
                    if (!Blank(r.Reference)) { (byReference.TryGetValue(r.Reference!, out var l1) ? l1 : byReference[r.Reference!] = []).Add(r); }
                }
                else if (r.Date is { } d)
                {
                    var k = (r.Amount.Currency, r.Amount.Amount, d);
                    (byComposite.TryGetValue(k, out var l2) ? l2 : byComposite[k] = []).Add(r);
                }
            }
            foreach (var l in _left)
            {
                IReadOnlyList<MatchItem> candidates = NoItems;
                if (index == EdgeIndex.Reference)
                {
                    if (!Blank(l.Reference) && byReference.TryGetValue(l.Reference!, out var c1)) { candidates = c1; }
                }
                else if (l.Date is { } d && byComposite.TryGetValue((l.Amount.Currency, l.Amount.Amount, d), out var c2))
                {
                    candidates = c2;
                }
                foreach (var r in candidates)
                {
                    if (edge(l, r))
                    {
                        edges.Add((l, r));
                    }
                }
            }
            var lDeg = edges.GroupBy(e => e.L.Key).ToDictionary(g => g.Key, g => g.Count());
            var rDeg = edges.GroupBy(e => e.R.Key).ToDictionary(g => g.Key, g => g.Count());
            var rest = new List<(MatchItem L, MatchItem R)>();
            foreach (var e in edges)
            {
                if (lDeg[e.L.Key] == 1 && rDeg[e.R.Key] == 1)
                {
                    Commit(ruleId, status, [e.L], [e.R], amountTolerant);
                }
                else
                {
                    rest.Add(e);
                }
            }
            Ambiguous(rest, ruleId);
        }

        private void Ambiguous(List<(MatchItem L, MatchItem R)> edges, string ruleId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in edges)
            {
                if (seen.Contains(start.L.Key))
                {
                    continue;
                }
                var ls = new Dictionary<string, MatchItem>(StringComparer.Ordinal);
                var rs = new Dictionary<string, MatchItem>(StringComparer.Ordinal);
                ls[start.L.Key] = start.L;
                rs[start.R.Key] = start.R;
                var grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var e in edges)
                    {
                        if ((ls.ContainsKey(e.L.Key) || rs.ContainsKey(e.R.Key)) && (ls.TryAdd(e.L.Key, e.L) | rs.TryAdd(e.R.Key, e.R)))
                        {
                            grew = true;
                        }
                    }
                }
                foreach (var k in ls.Keys)
                {
                    seen.Add(k);
                }
                var lItems = Sorted(ls.Values);
                var rItems = Sorted(rs.Values);
                var all = lItems.Concat(rItems).Select(i => i.Ref).ToList();
                var ccy = lItems[0].Amount.Currency;
                var text = $"Ambiguous: {lItems.Count} internal and {rItems.Count} counterpart record(s) are equally valid candidates for rule {ruleId}; " +
                           "no automatic match was made. Candidates: " + string.Join(", ", all.Select(r => $"{r.SourceName}/{r.SourceRowId}")) + ". Manual decision required.";
                Add(MatchGroupStatus.Unmatched, Card(lItems.Count, rItems.Count), ruleId, MatchingCodes.AmbiguousCandidates,
                    ccy, null, null, text, lItems, rItems, all);
                Remove(lItems, rItems);
            }
        }

        private void Grouped()
        {
            Compact();
            var lefts = _left.Where(l => !Blank(l.Reference)).GroupBy(l => (l.Reference!, l.Amount.Currency));
            var rightIndex = new Dictionary<(string, string), List<MatchItem>>();
            foreach (var r in _right)
            {
                if (r.Reference is { } rr) { (rightIndex.TryGetValue((rr, r.Amount.Currency), out var l) ? l : rightIndex[(rr, r.Amount.Currency)] = []).Add(r); }
            }
            var plan = new List<(List<MatchItem> L, List<MatchItem> R)>();
            foreach (var g in lefts.OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal))
            {
                var ls = g.ToList();
                var rs = rightIndex.TryGetValue(g.Key, out var found) ? found.ToList() : [];
                if (rs.Count == 0 || (ls.Count == 1 && rs.Count == 1))
                {
                    continue;
                }
                plan.Add((ls, rs));
            }
            foreach (var (ls, rs) in plan)
            {
                var all = ls.Concat(rs).ToList();
                if (!all.All(a => all.All(b => DateOk(a, b))))
                {
                    continue;
                }
                if (ls.Count > 1 && rs.Count > 1)
                {
                    var refs = Sorted(all).Select(i => i.Ref).ToList();
                    Add(MatchGroupStatus.Unmatched, "N:M", MatchingCodes.GroupedMatch, MatchingCodes.AmbiguousCandidates, ls[0].Amount.Currency, null, null,
                        $"Ambiguous: {ls.Count} internal and {rs.Count} counterpart records share reference '{ls[0].Reference}'; a controlled group requires one side to be a single record. Candidates: " +
                        string.Join(", ", refs.Select(r => $"{r.SourceName}/{r.SourceRowId}")) + ".", ls, rs, refs);
                    Remove(ls, rs);
                    continue;
                }
                var ccy = ls[0].Amount.Currency;
                var diff = new Money(ls.Sum(x => x.Amount.Amount) - rs.Sum(x => x.Amount.Amount), ccy);
                var within = Math.Abs(diff.Amount) <= o.ToleranceFor(ccy);
                if (within)
                {
                    Commit(MatchingCodes.GroupedMatch, MatchGroupStatus.Matched, ls, rs, true);
                }
                else
                {
                    Add(MatchGroupStatus.ValueMismatch, Card(ls.Count, rs.Count), MatchingCodes.GroupedMatch, FindingCodes.ValueMismatch, ccy, diff, null,
                        $"Grouped candidates share reference '{ls[0].Reference}' but totals differ by {diff} (internal {new Money(ls.Sum(x => x.Amount.Amount), ccy)}, counterpart {new Money(rs.Sum(x => x.Amount.Amount), ccy)}), beyond tolerance {new Money(o.ToleranceFor(ccy), ccy)}.",
                        ls, rs, []);
                    Remove(ls, rs);
                }
            }
        }

        private void Commit(string ruleId, MatchGroupStatus status, List<MatchItem> ls, List<MatchItem> rs, bool amountTolerant)
        {
            var lc = ls[0].Amount.Currency;
            if (status == MatchGroupStatus.Unmatched)
            {
                // currency mismatch: no difference can be computed across currencies
                var text = $"Same reference '{ls[0].Reference}' but currencies differ ({lc} vs {rs[0].Amount.Currency}); amounts are never netted or converted.";
                Add(status, "1:1", ruleId, MatchingCodes.CurrencyMismatch, lc, null, null, text, ls, rs, [.. ls.Concat(rs).Select(i => i.Ref)]);
                Remove(ls, rs);
                return;
            }
            var diff = new Money(ls.Sum(x => x.Amount.Amount) - rs.Sum(x => x.Amount.Amount), lc);
            var dd = ls.Count == 1 && rs.Count == 1 ? DateDiff(ls[0], rs[0]) : null;
            var tol = o.ToleranceFor(lc);
            string text2;
            string reason = ruleId;
            if (status == MatchGroupStatus.ValueMismatch)
            {
                text2 = $"Reference '{ls[0].Reference}' and currency agree but the amount differs by {diff} ({ls[0].Amount} vs {rs[0].Amount}), beyond tolerance {new Money(tol, lc)}.";
                reason = FindingCodes.ValueMismatch;
            }
            else
            {
                text2 = ruleId switch
                {
                    MatchingCodes.ExactIdentifierMatch => $"Reference '{ls[0].Reference}', amount {ls[0].Amount} and value date agree exactly.",
                    MatchingCodes.ExactCompositeMatch => $"No usable reference; amount {ls[0].Amount}, currency and value date agree exactly.",
                    MatchingCodes.DateToleranceMatch => $"Reference and amount agree; value dates differ by {dd} day(s), within the {o.WindowFor(lc)}-day window.",
                    MatchingCodes.AmountToleranceMatch => $"Reference agrees; amount differs by {diff}, within tolerance {new Money(tol, lc)}.",
                    _ => $"Controlled group {Card(ls.Count, rs.Count)} on reference '{ls[0].Reference}': totals differ by {diff}, within tolerance {new Money(tol, lc)}.",
                };
            }
            Add(status, Card(ls.Count, rs.Count), ruleId, reason, lc, diff, dd, text2, ls, rs, []);
            Remove(ls, rs);
        }

        private void Leftovers()
        {
            Compact();
            foreach (var l in _left.ToList())
            {
                Add(MatchGroupStatus.Unmatched, "1:0", MatchingCodes.NoCandidate, MatchingCodes.NoCandidate, l.Amount.Currency, null, null,
                    $"No counterpart candidate for {l.Source}/{l.Id} ({l.Amount}, reference '{l.Reference}').", [l], [], []);
            }
            foreach (var r in _right.ToList())
            {
                Add(MatchGroupStatus.Unmatched, "0:1", MatchingCodes.NoCandidate, MatchingCodes.NoCandidate, r.Amount.Currency, null, null,
                    $"No internal candidate for {r.Source}/{r.Id} ({r.Amount}, reference '{r.Reference}').", [], [r], []);
            }
            _left.Clear();
            _right.Clear();
        }

        private static string Card(int l, int r) => (l, r) switch
        {
            (1, 1) => "1:1",
            (1, _) => "1:N",
            (_, 1) => "N:1",
            _ => "N:M",
        };

        private void Remove(IEnumerable<MatchItem> ls, IEnumerable<MatchItem> rs)
        {
            foreach (var i in ls) { _removedLeft.Add(i.Key); }
            foreach (var i in rs) { _removedRight.Add(i.Key); }
        }

        private void Add(MatchGroupStatus status, string cardinality, string ruleId, string reason, string ccy, Money? diff, int? dateDiff,
            string rationale, IEnumerable<MatchItem> ls, IEnumerable<MatchItem> rs, IReadOnlyList<RecordRef> candidates)
        {
            var lr = Sorted(ls).Select(i => i.Ref).ToList();
            var rr = Sorted(rs).Select(i => i.Ref).ToList();
            var id = $"MG:{pair.Name}:{ruleId}:{reason}:" + string.Join(",", lr.Select(r => r.SourceName + "/" + r.SourceRowId)) + "|" +
                     string.Join(",", rr.Select(r => r.SourceName + "/" + r.SourceRowId));
            output.Add(new MatchGroup(id, pair.Name, status, cardinality, ruleId, o.RuleVersion, reason,
                new Money(o.ToleranceFor(ccy), ccy), o.WindowFor(ccy), diff, dateDiff, rationale, lr, rr,
                candidates.OrderBy(r => r.SourceName, StringComparer.Ordinal).ThenBy(r => r.SourceRowId, StringComparer.Ordinal).ToList()));
        }
    }
}
