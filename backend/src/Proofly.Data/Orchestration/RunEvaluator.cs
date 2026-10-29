using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Proofly.Controls.Common;
using Proofly.Controls.Completeness;
using Proofly.Controls.Ledger;
using Proofly.Controls.Lifecycle;
using Proofly.Controls.Matching;
using Proofly.Controls.Safeguarding;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Orchestration;

/// <summary>
/// Pure and deterministic: runs the five controls over the loaded inputs (completeness first, its gate applied to the
/// downstream results) and shapes the outcome into what is persisted. No database, no clock, no randomness: the same
/// inputs and options give the same <see cref="RunEvaluation.ResultHash"/>. Orderings never depend on database ids.
/// </summary>
public static class RunEvaluator
{
    private static readonly StringComparer Ord = StringComparer.Ordinal;

    public static string StableId(string prefix, string runId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(runId + "|" + key));
        return prefix + Convert.ToHexString(hash)[..16];
    }

    public static string ExceptionId(string runId, string findingKey) => StableId("EXC-", runId, findingKey);
    public static string MatchGroupId(string runId, string groupId) => StableId("MG-", runId, groupId);

    public static RunEvaluation Evaluate(RunInputs inputs, RunProcessingOptions options)
    {
        var date = inputs.BusinessDate;
        var recs = inputs.Records.OrderBy(r => r.Table, Ord).ThenBy(r => r.Record.SourceRowId, Ord).ThenBy(r => r.DeliveryId, Ord).ToList();
        List<T> Of<T>() where T : ISourceRecord => recs.Select(r => r.Record).OfType<T>().ToList();
        var transfers = Of<TransferInstruction>();
        var debits = Of<DirectDebitEvent>();
        var cardEvents = Of<CardEvent>();
        var settlements = Of<CardSettlement>();
        var ledger = Of<LedgerEntry>();
        var balances = Of<AccountBalance>();
        var bank = Of<BankTransaction>();
        var sgBalances = Of<SafeguardingBalance>();

        // 1. Completeness first; its result gates everything downstream.
        var completeness = DataCompletenessControl.Evaluate(new CompletenessInput(date, inputs.MandatorySources,
            inputs.Deliveries.Select(d => d.Info).OrderBy(d => d.DeliveryId, Ord).ToList(),
            inputs.Rejections.OrderBy(r => r.DeliveryId, Ord).ThenBy(r => r.RowNumber).ToList()), options.Completeness);

        // 2. Independent ledger. The opening balance is the one delivered with the daily rollforward, with that delivery as its provenance.
        var openings = recs.Where(r => r.Record is AccountBalance { OpeningBalance: not null, BalanceType: AccountBalanceType.DailyRollforward } b && b.BalanceDate == date)
            .Select(r => { var b = (AccountBalance)r.Record; return new OpeningBalanceEvidence(b.AccountId, b.OpeningBalance!.Value, $"{SourceNames.ClientAccountBalances}:{r.DeliveryId}"); })
            .ToList();
        var ledgerEval = IndependentLedgerControl.Calculate(new LedgerInput(date, openings, ledger, bank, transfers, balances), options.Ledger);

        // 3. Matching, 4. lifecycle.
        var matchInput = MatchingInput.FromRecords(transfers, ledger, debits, settlements, cardEvents, bank);
        var matching = MatchingControl.Evaluate(matchInput, options.Matching);
        var lifecycle = TransactionLifecycleControl.EvaluateDetailed(
            new LifecycleInput(date, transfers, debits, cardEvents, settlements, ledger, bank), options.Lifecycle);

        // 5. Safeguarding: obligations are the independent client balances; only CLOSING_AVAILABLE counts as coverage (MVP eligibility policy).
        var obligations = ledgerEval.Balances.Where(b => b.Independent is not null)
            .Select(b => new ClientObligationInput(b.AccountId, b.Currency, b.Independent!.Value.Amount)).ToList();
        var externals = sgBalances.Select(b => new ExternalBalanceInput(b.SourceRowId, b.ExternalAccountId, b.Balance, b.SourceTimestamp,
            b.BalanceType == SafeguardingBalanceType.ClosingAvailable, b.BalanceType == SafeguardingBalanceType.ClosingAvailable ? null : "CLOSING_LEDGER_NOT_COVERAGE_BASIS")).ToList();
        var asOf = sgBalances.Count > 0 ? sgBalances.Max(b => b.SourceTimestamp) : new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var safeguarding = SafeguardingCoverageControl.Evaluate(asOf, date, obligations, externals, inputs.Adjustments, options.Safeguarding);

        var gated = new[] { ledgerEval.Result, matching.Control, lifecycle.Control, safeguarding.Control }
            .Select(c => DataCompletenessControl.ApplyGate(c, completeness)).ToList();
        var results = new[] { completeness }.Concat(gated).ToList();

        var recordCounts = new Dictionary<ControlArea, int>
        {
            [ControlArea.DataCompleteness] = inputs.Deliveries.Sum(d => d.Info.ActualCount),
            [ControlArea.Matching] = matching.Groups.Count,
            [ControlArea.IndependentLedger] = ledger.Count,
            [ControlArea.TransactionLifecycle] = transfers.Count + debits.Count + cardEvents.Count,
            [ControlArea.Safeguarding] = safeguarding.Positions.Count,
        };
        var controls = results.Select(r => new ControlOutcome(r.Area, r.State, r.RuleVersion, recordCounts[r.Area], r.Findings.Count, r.Findings.Count(f => f.Blocking))).ToList();

        // Exceptions: one per finding (first occurrence of a key wins), ordered by key.
        var byRef = new Dictionary<(string, string), LoadedRecord>();
        foreach (var r in recs) byRef.TryAdd((r.SourceName, r.Record.SourceRowId), r);
        var deliveriesById = inputs.Deliveries.ToDictionary(d => d.Info.DeliveryId, Ord);
        var rejectionDelivery = new Dictionary<(string, string), string>();
        foreach (var x in inputs.Rejections) rejectionDelivery.TryAdd((x.SourceName, x.SourceRowId ?? $"row{x.RowNumber.ToString(CultureInfo.InvariantCulture)}"), x.DeliveryId);

        var groupByFinding = matching.Groups.ToDictionary(g => $"matching:{g.ReasonCode}:{g.Id}", g => g, Ord);
        var toleranceByGroup = matching.Groups.ToDictionary(g => g.Id, ToleranceText, Ord);
        var exceptions = new List<ExceptionDraft>();
        var seen = new HashSet<string>(Ord);
        foreach (var f in results.SelectMany(r => r.Findings).OrderBy(f => f.Key, Ord))
        {
            if (!seen.Add(f.Key)) continue;
            var links = f.Records.Select(r => byRef.GetValueOrDefault((r.SourceName, r.SourceRowId))).Where(r => r is not null)
                .Select(r => new RecordLink(r!.Table, r.DbId)).Distinct().OrderBy(l => l.Table, Ord).ThenBy(l => l.DbId).ToList();
            string? deliveryId = null, file = null, row = null;
            // The finding's own subject first (its Primary), then the supporting records in order.
            var primary = (f.Primary is { } p0 ? f.Records.Prepend(p0) : f.Records)
                .Select(r => (Ref: r, Rec: byRef.GetValueOrDefault((r.SourceName, r.SourceRowId)))).FirstOrDefault(x => x.Rec is not null);
            if (primary.Rec is not null)
            {
                deliveryId = primary.Rec.DeliveryId;
                row = primary.Ref.SourceRowId;
            }
            else
            {
                foreach (var r in f.Records)
                {
                    if (deliveriesById.ContainsKey(r.SourceRowId)) { deliveryId = r.SourceRowId; row = null; break; }
                    if (rejectionDelivery.TryGetValue((r.SourceName, r.SourceRowId), out var rd)) { deliveryId = rd; row = r.SourceRowId; break; }
                }
            }
            if (deliveryId is not null && deliveriesById.TryGetValue(deliveryId, out var del)) file = del.FileName;

            var area = WireNames.ToLowerSnake(f.Area);
            string? tolerance = f.Area == ControlArea.IndependentLedger && f.Value is { } v ? $"{options.Ledger.Tolerance.ToString(CultureInfo.InvariantCulture)} {v.Currency}" : null;
            if (groupByFinding.TryGetValue(f.Key, out var mg)) tolerance = toleranceByGroup[mg.Id];
            var calc = new JsonObject
            {
                ["inputs"] = new JsonArray(f.Records.Select(r => (JsonNode)new JsonObject { ["name"] = "record", ["value"] = $"{r.SourceName}:{r.SourceRowId}" }).ToArray()),
                ["text"] = f.Description,
                ["result"] = f.Value is { } m ? m.ToString() : f.ReasonCode,
            };
            exceptions.Add(new ExceptionDraft(ExceptionId(inputs.RunId, f.Key), f, FindingText.Headline(f), FindingText.Detail(f),
                new DateTimeOffset(date.AddDays(options.DueDays(f.Severity)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                deliveryId, file, row, links, $"{area}.{f.ReasonCode}", tolerance,
                f.Blocking ? "Blocks run sign-off until resolved or accepted with approval" : "Requires review; does not block run sign-off",
                calc.ToJsonString()));
        }
        var exceptionIdByKey = exceptions.ToDictionary(e => e.Finding.Key, e => e.Id, Ord);

        // Match groups with candidates and the exceptions raised for them.
        var pairs = matchInput.Pairs.ToDictionary(p => p.Name, Ord);
        // Per pair, built once: a rebuild per group would scan the whole pair for every one of (up to) 100,000 groups.
        var pairIndex = new Dictionary<string, (Dictionary<string, MatchItem> Items, HashSet<string> LeftKeys)>(Ord);
        var groups = new List<MatchGroupDraft>();
        foreach (var g in matching.Groups)
        {
            var pair = pairs[g.Pair];
            if (!pairIndex.TryGetValue(g.Pair, out var idx))
            {
                var all0 = new Dictionary<string, MatchItem>(Ord);
                foreach (var i in pair.Left.Concat(pair.Right)) all0.TryAdd(i.Key, i); // first occurrence wins, as GroupBy(...).First() did
                pairIndex[g.Pair] = idx = (all0, pair.Left.Select(i => i.Key).ToHashSet(Ord));
            }
            var items = idx.Items;
            var leftKeys = idx.LeftKeys;
            var cands = new List<MatchCandidateDraft>();
            var added = new HashSet<string>(Ord);
            void Add(RecordRef r, string side, bool selected)
            {
                var key = r.SourceName + "/" + r.SourceRowId;
                if (!items.TryGetValue(key, out var item) || !byRef.TryGetValue((r.SourceName, r.SourceRowId), out var rec) || !added.Add(side + key)) return;
                cands.Add(new MatchCandidateDraft(RecordKinds.CandidateKind(r.SourceName), side, rec.Table, rec.DbId, item.Amount, selected));
            }
            var paired = g.Status != MatchGroupStatus.Unmatched;
            foreach (var r in g.Left) Add(r, "LEFT", paired);
            foreach (var r in g.Right) Add(r, "RIGHT", paired);
            foreach (var r in g.Candidates) Add(r, leftKeys.Contains(r.SourceName + "/" + r.SourceRowId) ? "LEFT" : "RIGHT", false);
            var ex = exceptionIdByKey.TryGetValue($"matching:{g.ReasonCode}:{g.Id}", out var eid) ? new[] { eid } : [];
            groups.Add(new MatchGroupDraft(MatchGroupId(inputs.RunId, g.Id), g, toleranceByGroup[g.Id], cands, ex));
        }

        // Safeguarding positions with their detail snapshot.
        var completenessQualification = completeness.State == ControlState.Passed ? null : $"data completeness is {completeness.State}";
        var unsupported = ledgerEval.Balances.Where(b => b.Independent is null).Select(b => $"independent balance not computable for account {b.AccountId} ({b.Currency})").ToList();
        var positions = safeguarding.Positions.Select(p => new PositionDraft(p, PositionDetail(p, safeguarding.Control, results, exceptionIdByKey,
            completenessQualification, unsupported.Where(u => u.EndsWith($"({p.Currency})", StringComparison.Ordinal))))).ToList();

        var hash = Hash(options.RulesetVersion, controls, results, matching.Groups, ledgerEval.Balances, safeguarding.Positions);
        return new RunEvaluation(options.RulesetVersion, controls, exceptions, groups, ledgerEval.Balances, positions, completeness.State, hash);
    }

    private static string ToleranceText(MatchGroup g) =>
        $"{(g.ToleranceUsed is { } t ? t.ToString() : "none")}; date window {g.DateWindowDaysUsed} days";

    private static string A(decimal d) => d.ToString("0.00####", CultureInfo.InvariantCulture);
    private static string M(Money? m) => m is { } v ? $"{A(v.Amount)} {v.Currency}" : "-";

    private static string PositionDetail(CurrencyPosition p, ControlResult sgControl, IReadOnlyList<ControlResult> all,
        IReadOnlyDictionary<string, string> exceptionIdByKey, string? completenessQualification, IEnumerable<string> unsupported)
    {
        var findingIds = new Dictionary<string, SortedSet<string>>(Ord);
        foreach (var f in sgControl.Findings.Where(f => exceptionIdByKey.ContainsKey(f.Key)))
            foreach (var r in f.Records.Where(r => r.SourceName == SourceNames.SafeguardingBalances))
                (findingIds.TryGetValue(r.SourceRowId, out var s) ? s : findingIds[r.SourceRowId] = new SortedSet<string>(Ord)).Add(exceptionIdByKey[f.Key]);

        var qualifications = new List<string>();
        if (completenessQualification is not null) qualifications.Add(completenessQualification);
        qualifications.AddRange(unsupported.OrderBy(u => u, Ord));
        qualifications.AddRange(p.BalanceLines.Where(l => !l.Included).Select(l => $"balance {l.SourceRowId} excluded: {l.ExclusionReason}"));
        qualifications.AddRange(p.AdjustmentLines.Where(l => !l.Included).Select(l => $"adjustment {l.AdjustmentId} excluded: {l.ExclusionReason}"));
        if (p.Position is null) qualifications.Add("coverage cannot be established: no external balance for this currency while an obligation exists");

        var root = new JsonObject
        {
            ["approvedAdjustments"] = A(p.ApprovedAdjustments.Amount),
            ["qualifications"] = new JsonArray(qualifications.Select(q => (JsonNode)JsonValue.Create(q)!).ToArray()),
            ["obligationLines"] = new JsonArray(p.ObligationLines.Select(l => (JsonNode)new JsonObject { ["accountId"] = l.AccountId, ["amount"] = A(l.Amount.Amount) }).ToArray()),
            ["balanceLines"] = new JsonArray(p.BalanceLines.Select(l => (JsonNode)new JsonObject
            {
                ["sourceRowId"] = l.SourceRowId,
                ["included"] = l.Included,
                ["reason"] = l.Included ? "ELIGIBLE" : l.ExclusionReason,
                ["findingIds"] = new JsonArray((findingIds.TryGetValue(l.SourceRowId, out var ids) ? ids : []).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
            }).ToArray()),
        };
        return root.ToJsonString();
    }

    /// <summary>Canonical text of the control outcome (ordered, invariant formatting, no ids or timestamps) hashed with SHA-256.</summary>
    private static string Hash(string ruleset, IReadOnlyList<ControlOutcome> controls, IReadOnlyList<ControlResult> results,
        IReadOnlyList<MatchGroup> groups, IReadOnlyList<IndependentBalance> balances, IReadOnlyList<CurrencyPosition> positions)
    {
        var sb = new StringBuilder();
        void L(params string[] parts) => sb.Append(string.Join("|", parts.Select(p => p.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\n", "\\n")))).Append('\n');
        L("ruleset", ruleset);
        foreach (var c in controls.OrderBy(c => c.Area)) L("control", c.Area.ToString(), c.State.ToString(), c.RuleVersion, c.RecordCount.ToString(CultureInfo.InvariantCulture));
        foreach (var f in results.SelectMany(r => r.Findings).OrderBy(f => f.Key, Ord))
            L("finding", f.Key, f.Area.ToString(), f.ReasonCode, f.Severity.ToString(), f.Blocking ? "1" : "0", M(f.Value), f.RuleVersion, f.Description,
                string.Join(",", f.Records.Select(r => r.SourceName + ":" + r.SourceRowId).OrderBy(x => x, Ord)));
        foreach (var g in groups.OrderBy(g => g.Id, Ord))
            L("group", g.Id, g.Pair, g.Status.ToString(), g.Cardinality, g.RuleId, g.RuleVersion, g.ReasonCode, M(g.ToleranceUsed), g.DateWindowDaysUsed.ToString(CultureInfo.InvariantCulture),
                M(g.ValueDifference), g.DateDifferenceDays?.ToString(CultureInfo.InvariantCulture) ?? "-", g.Rationale,
                string.Join(",", g.Left.Select(r => r.SourceName + ":" + r.SourceRowId)), string.Join(",", g.Right.Select(r => r.SourceName + ":" + r.SourceRowId)),
                string.Join(",", g.Candidates.Select(r => r.SourceName + ":" + r.SourceRowId)));
        foreach (var b in balances.OrderBy(b => b.AccountId, Ord).ThenBy(b => b.Currency, Ord))
            L("balance", b.AccountId, b.Currency, M(b.Opening), b.OpeningSupported ? "1" : "0", M(b.Credits), M(b.Debits), M(b.Adjustments), M(b.Independent), M(b.Recorded),
                M(b.Difference), M(b.UnexplainedResidual),
                string.Join(",", b.Contributions.Select(c => $"{c.Record.SourceName}:{c.Record.SourceRowId}:{c.Kind}:{c.Direction}:{M(c.Amount)}:{M(c.IndependentEffect)}:{M(c.RecordedEffect)}")),
                string.Join(",", b.Exclusions.Select(x => $"{x.Record.SourceName}:{x.Record.SourceRowId}:{x.Reason}")));
        foreach (var p in positions.OrderBy(p => p.Currency, Ord))
            L("position", p.Currency, M(p.Obligation), M(p.EligibleTotal), M(p.ApprovedAdjustments), M(p.Position), p.State.ToString(),
                string.Join(",", p.ObligationLines.Select(l => $"{l.AccountId}:{M(l.Amount)}")),
                string.Join(",", p.BalanceLines.Select(l => $"{l.SourceRowId}:{l.Included}:{l.ExclusionReason}")),
                string.Join(",", p.AdjustmentLines.Select(l => $"{l.AdjustmentId}:{M(l.Amount)}:{l.Included}:{l.ExclusionReason}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
