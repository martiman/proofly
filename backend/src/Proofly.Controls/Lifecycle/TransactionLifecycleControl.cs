using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Lifecycle;

/// <summary>
/// Pure, deterministic lifecycle verification for bank transfers, direct debits and card transactions.
/// Joins are by ids only: ledger.TransactionReference = instruction / card transaction id, bank.BankReference =
/// transfer ExternalReference or card settlement id.
/// </summary>
public static class TransactionLifecycleControl
{
    public static ControlResult Evaluate(LifecycleInput input, LifecycleOptions? options = null)
        => EvaluateDetailed(input, options).Control;

    public static LifecycleResult EvaluateDetailed(LifecycleInput input, LifecycleOptions? options = null)
    {
        var ctx = new Ctx(input, options ?? new LifecycleOptions());
        Transfers(ctx);
        DirectDebits(ctx);
        Cards(ctx);
        var verdicts = ctx.Verdicts
            .OrderBy(v => v.Kind).ThenBy(v => v.TransactionId, StringComparer.Ordinal).ToList();
        return new LifecycleResult(
            ControlResult.From(ControlArea.TransactionLifecycle, ctx.O.RuleVersion, ctx.Findings.DistinctBy(f => f.Key)),
            verdicts);
    }

    private sealed class Ctx(LifecycleInput i, LifecycleOptions o)
    {
        public readonly LifecycleInput I = i;
        public readonly LifecycleOptions O = o;
        public readonly List<Finding> Findings = [];
        public readonly List<LifecycleVerdict> Verdicts = [];

        public int Age(DateOnly from) => BusinessDays(from, I.BusinessDate, O.Holidays);

        public void Add(string code, string source, string rowId, string desc, IEnumerable<RecordRef> refs,
            Money? value = null, bool timing = false)
            => Findings.Add(new Finding($"lifecycle:{code}:{source}:{rowId}", ControlArea.TransactionLifecycle, code,
                timing ? O.TimingSeverity : O.DefaultSeverity, desc, refs.ToList(), value, O.RuleVersion,
                O.BlockingCodes.Contains(code), new RecordRef(source, rowId)));
    }

    /// <summary>Weekdays (not holidays) in (from, to]; 0 when to is not after from.</summary>
    public static int BusinessDays(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays)
    {
        var n = 0;
        for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d)) n++;
        }
        return n;
    }

    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.DateTime);
    private static RecordRef R(string s, string id) => new(s, id);

    private static void Verdict(Ctx c, LifecycleKind k, string id, string stage, string? next, LifecycleState st,
        LifecycleTiming t, int age, IEnumerable<RecordRef> refs)
        => c.Verdicts.Add(new LifecycleVerdict(k, id, stage, next, st, t, age, refs.ToList()));

    // ---------------------------------------------------------------- transfers
    private static void Transfers(Ctx c)
    {
        var known = c.I.Transfers.Select(t => t.InstructionId).ToHashSet(StringComparer.Ordinal);
        var ledger = c.I.LedgerEntries.Where(l => l.TransactionReference is not null)
            .ToLookup(l => l.TransactionReference!, StringComparer.Ordinal);
        var bank = c.I.BankTransactions.Where(b => b.BankReference is not null)
            .ToLookup(b => b.BankReference!, StringComparer.Ordinal);

        foreach (var l in c.I.LedgerEntries.Where(l => l.EntryType == LedgerEntryType.Transfer
                     && l.TransactionReference is not null && !known.Contains(l.TransactionReference)))
        {
            c.Add(FindingCodes.MissingInstruction, SourceNames.InternalLedgerEntries, l.LedgerEntryId,
                $"Transfer ledger entry {l.LedgerEntryId} references unknown instruction {l.TransactionReference}.",
                [R(SourceNames.InternalLedgerEntries, l.LedgerEntryId)], l.Amount);
        }

        foreach (var t in c.I.Transfers)
        {
            var src = SourceNames.TransferInstructions;
            var self = R(src, t.SourceRowId);
            var age = c.Age(Day(t.InstructionTimestamp));
            var overdue = age > c.O.TransferWindowBusinessDays;
            var timing = overdue ? LifecycleTiming.Overdue : LifecycleTiming.Within;
            var posted = ledger[t.InstructionId].Where(l => l.SourceStatus == LedgerEntryStatus.Posted).ToList();
            var postedDebits = posted.Where(l => l.EntryType == LedgerEntryType.Transfer && l.DebitCredit == DebitCredit.Debit).ToList();
            var reversed = posted.Any(l => l.EntryType == LedgerEntryType.Reversal);
            var bankRows = t.ExternalReference is null ? []
                : bank[t.ExternalReference].Where(b => b.Status == BankTransactionStatus.Booked).ToList();
            var refs = new List<RecordRef> { self };
            refs.AddRange(posted.Select(l => R(SourceNames.InternalLedgerEntries, l.LedgerEntryId)));
            refs.AddRange(bankRows.Select(b => R(SourceNames.BankingTransactions, b.ExternalTransactionId)));

            switch (t.SourceStatus)
            {
                case TransferStatus.Rejected or TransferStatus.Cancelled:
                    var bad = false;
                    if (postedDebits.Count > 0 && !reversed)
                    {
                        bad = true;
                        foreach (var d in postedDebits)
                        {
                            c.Add(FindingCodes.InvalidState, SourceNames.InternalLedgerEntries, d.LedgerEntryId,
                                $"{t.SourceStatus} transfer {t.InstructionId} has a posted debit and no reversing entry.",
                                [self, R(SourceNames.InternalLedgerEntries, d.LedgerEntryId)], d.Amount);
                        }
                    }
                    if (bankRows.Count > 0)
                    {
                        bad = true;
                        c.Add(FindingCodes.InvalidState, src, t.SourceRowId,
                            $"{t.SourceStatus} transfer {t.InstructionId} has booked bank evidence {t.ExternalReference}.",
                            refs, t.Amount);
                    }
                    Verdict(c, LifecycleKind.Transfer, t.InstructionId, t.SourceStatus.ToString(), null,
                        bad ? LifecycleState.Exception : LifecycleState.Completed, LifecycleTiming.NotApplicable, age, refs);
                    break;

                case TransferStatus.Settled:
                    var missing = new List<string>();
                    if (postedDebits.Count == 0) missing.Add(LifecycleCodes.MissingInternalPosting);
                    if (bankRows.Count == 0) missing.Add(LifecycleCodes.MissingExternalOutcome);
                    if (missing.Count == 0)
                    {
                        var bookedDay = bankRows.Min(b => b.ValueDate);
                        var late = BusinessDays(Day(t.InstructionTimestamp), bookedDay, c.O.Holidays) > c.O.TransferWindowBusinessDays;
                        if (late)
                        {
                            c.Add(LifecycleCodes.LateOutcome, src, t.SourceRowId,
                                $"Transfer {t.InstructionId} reached the bank on {bookedDay:yyyy-MM-dd}, beyond its {c.O.TransferWindowBusinessDays}-business-day window.",
                                refs, t.Amount, timing: true);
                        }
                        Verdict(c, LifecycleKind.Transfer, t.InstructionId, "Executed", null, LifecycleState.Completed,
                            late ? LifecycleTiming.Overdue : LifecycleTiming.Within, age, refs);
                    }
                    else if (!overdue)
                    {
                        Verdict(c, LifecycleKind.Transfer, t.InstructionId, "Settled (evidence incomplete)",
                            string.Join("+", missing), LifecycleState.Pending, timing, age, refs);
                    }
                    else
                    {
                        foreach (var code in missing)
                        {
                            c.Add(code, src, t.SourceRowId,
                                $"Transfer {t.InstructionId} is settled but {(code == LifecycleCodes.MissingInternalPosting ? "has no posted ledger debit" : "has no booked bank evidence")} after {age} business days (window {c.O.TransferWindowBusinessDays}).",
                                refs, t.Amount);
                        }
                        Verdict(c, LifecycleKind.Transfer, t.InstructionId, "Settled (evidence incomplete)",
                            string.Join("+", missing), LifecycleState.Exception, timing, age, refs);
                    }
                    break;

                default: // Submitted
                    if (!overdue)
                    {
                        Verdict(c, LifecycleKind.Transfer, t.InstructionId, "Submitted", "Executed",
                            LifecycleState.Pending, timing, age, refs);
                    }
                    else
                    {
                        c.Add(LifecycleCodes.Stalled, src, t.SourceRowId,
                            $"Transfer {t.InstructionId} is still submitted after {age} business days (window {c.O.TransferWindowBusinessDays}).",
                            refs, t.Amount, timing: true);
                        Verdict(c, LifecycleKind.Transfer, t.InstructionId, "Submitted", "Executed",
                            LifecycleState.Exception, timing, age, refs);
                    }
                    break;
            }
        }
    }

    // ------------------------------------------------------------ direct debits
    private static int Rank(DirectDebitEventType t) => (int)t;

    private static bool Allowed(DirectDebitEventType from, DirectDebitEventType to) => (from, to) switch
    {
        (DirectDebitEventType.Submitted, DirectDebitEventType.Settled) => true,
        (DirectDebitEventType.Submitted, DirectDebitEventType.Rejected) => true,
        (DirectDebitEventType.Settled, DirectDebitEventType.Returned) => true,
        (DirectDebitEventType.Returned, DirectDebitEventType.Reversed) => true,
        _ => false,
    };

    private static void DirectDebits(Ctx c)
    {
        var src = SourceNames.DirectDebitEvents;
        var ledger = c.I.LedgerEntries.Where(l => l.TransactionReference is not null && l.SourceStatus == LedgerEntryStatus.Posted)
            .ToLookup(l => l.TransactionReference!, StringComparer.Ordinal);

        foreach (var g in c.I.DirectDebitEvents.GroupBy(e => e.InstructionId, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var id = g.Key;
            var ev = g.OrderBy(e => e.EventTimestamp).ThenBy(e => Rank(e.EventType)).ThenBy(e => e.SourceRowId, StringComparer.Ordinal).ToList();
            var refs = ev.Select(e => R(src, e.SourceRowId)).ToList();
            var posted = ledger[id].ToList();
            refs.AddRange(posted.Select(l => R(SourceNames.InternalLedgerEntries, l.LedgerEntryId)));
            var first = ev[0];
            var last = ev[^1];
            var exception = false;
            var money = first.Amount;
            var age = c.Age(first.CollectionDate ?? Day(first.EventTimestamp));

            if (!ev.Any(e => e.EventType == DirectDebitEventType.Submitted))
            {
                exception = true;
                foreach (var e in ev)
                {
                    c.Add(FindingCodes.MissingInstruction, src, e.SourceRowId,
                        $"{e.EventType} event for direct debit {id} references an instruction with no submission.",
                        [R(src, e.SourceRowId)], e.Amount);
                }
            }
            foreach (var e in ev.Where(e => string.IsNullOrWhiteSpace(e.MandateReference)))
            {
                exception = true;
                c.Add(FindingCodes.MissingAuthorization, src, e.SourceRowId,
                    $"Direct debit {id} has no mandate reference.", [R(src, e.SourceRowId)], e.Amount);
            }

            var hasSettled = ev.Any(e => e.EventType == DirectDebitEventType.Settled);
            var hasRejected = ev.Any(e => e.EventType == DirectDebitEventType.Rejected);
            if (hasSettled && hasRejected)
            {
                exception = true;
                c.Add(LifecycleCodes.ConflictingOutcomes, src, ev.First(e => e.EventType == DirectDebitEventType.Rejected).SourceRowId,
                    $"Direct debit {id} is both settled and rejected.", refs, money);
            }
            else
            {
                for (var k = 1; k < ev.Count; k++)
                {
                    if (ev[k - 1].EventType == ev[k].EventType && ev[k].EventType != DirectDebitEventType.Submitted
                        || !Allowed(ev[k - 1].EventType, ev[k].EventType) && ev[k - 1].EventType != ev[k].EventType)
                    {
                        exception = true;
                        c.Add(LifecycleCodes.InvalidTransition, src, ev[k].SourceRowId,
                            $"Direct debit {id}: transition {ev[k - 1].EventType} -> {ev[k].EventType} is not allowed.",
                            [R(src, ev[k - 1].SourceRowId), R(src, ev[k].SourceRowId)], ev[k].Amount);
                    }
                }
            }

            var reversing = posted.Where(l => l.EntryType == LedgerEntryType.Reversal).ToList();
            var returned = ev.FirstOrDefault(e => e.EventType == DirectDebitEventType.Returned);
            var revEvent = ev.FirstOrDefault(e => e.EventType == DirectDebitEventType.Reversed);
            if (returned is not null && reversing.Count == 0)
            {
                exception = true;
                c.Add(FindingCodes.ReturnNotPosted, src, returned.SourceRowId,
                    $"Direct debit {id} was returned ({returned.ReturnReason ?? "no reason given"}) but no reversing ledger entry exists.",
                    refs, returned.Amount);
            }
            if (revEvent is not null && reversing.Count == 0 && returned is null)
            {
                exception = true;
                c.Add(LifecycleCodes.ReversalNotPosted, src, revEvent.SourceRowId,
                    $"Direct debit {id} reversal has no reversing ledger entry.", refs, revEvent.Amount);
            }
            else if (revEvent is not null && reversing.Count == 0 && returned is not null)
            {
                c.Add(LifecycleCodes.ReversalNotPosted, src, revEvent.SourceRowId,
                    $"Direct debit {id} reversal has no reversing ledger entry.", refs, revEvent.Amount);
                exception = true;
            }

            var overdue = age > c.O.DirectDebitWindowBusinessDays;
            var timing = overdue ? LifecycleTiming.Overdue : LifecycleTiming.Within;
            string stage;
            string? next = null;
            var state = exception ? LifecycleState.Exception : LifecycleState.Completed;
            var terminalKnown = ev.Any(e => e.EventType is DirectDebitEventType.Settled or DirectDebitEventType.Rejected
                or DirectDebitEventType.Returned or DirectDebitEventType.Reversed);
            if (!terminalKnown)
            {
                stage = "Submitted";
                next = "Settled";
                if (!overdue) { if (!exception) state = LifecycleState.Pending; }
                else
                {
                    exception = true;
                    state = LifecycleState.Exception;
                    c.Add(LifecycleCodes.Stalled, src, first.SourceRowId,
                        $"Direct debit {id} has no outcome {age} business days after collection date (window {c.O.DirectDebitWindowBusinessDays}).",
                        refs, money, timing: true);
                }
            }
            else
            {
                stage = last.EventType.ToString();
                timing = LifecycleTiming.NotApplicable;
                if (hasSettled && returned is null && revEvent is null && !hasRejected
                    && !posted.Any(l => l.EntryType != LedgerEntryType.Reversal && l.DebitCredit == DebitCredit.Credit))
                {
                    // collection must be reflected internally; inside the window it is pending.
                    if (!overdue) { if (!exception) { state = LifecycleState.Pending; next = "Ledger posting"; } timing = LifecycleTiming.Within; }
                    else
                    {
                        state = LifecycleState.Exception;
                        timing = LifecycleTiming.Overdue;
                        c.Add(LifecycleCodes.MissingInternalPosting, src, ev.First(e => e.EventType == DirectDebitEventType.Settled).SourceRowId,
                            $"Direct debit {id} is collected but has no posted ledger credit after {age} business days.",
                            refs, money);
                    }
                }
            }
            Verdict(c, LifecycleKind.DirectDebit, id, stage, next, state, timing, age, refs);
        }
    }

    // -------------------------------------------------------------------- cards
    private static void Cards(Ctx c)
    {
        var src = SourceNames.CardTransactions;
        var journeys = c.I.CardEvents.GroupBy(e => e.CardTransactionId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var settlements = c.I.CardSettlements.GroupBy(s => s.SettlementId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bank = c.I.BankTransactions.Where(b => b.BankReference is not null && b.Status == BankTransactionStatus.Booked)
            .ToLookup(b => b.BankReference!, StringComparer.Ordinal);

        foreach (var (id, list) in journeys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var ev = list.OrderBy(e => e.EventTimestamp).ThenBy(e => (int)e.EventType).ThenBy(e => e.CardEventId, StringComparer.Ordinal).ToList();
            var refs = ev.Select(e => R(src, e.SourceRowId)).ToList();
            var age = c.Age(Day(ev[0].EventTimestamp));
            var exception = false;
            var pending = false;
            string? next = null;
            var timing = LifecycleTiming.NotApplicable;
            var auth = ev.FirstOrDefault(e => e.EventType == CardEventType.Authorization);
            var pres = ev.Where(e => e.EventType == CardEventType.Presentment).ToList();

            if (pres.Count > 0 && auth is null && c.O.RequireCardAuthorization)
            {
                exception = true;
                c.Add(FindingCodes.MissingAuthorization, src, pres[0].SourceRowId,
                    $"Card transaction {id} has a presentment but no authorisation event.", refs, pres[0].Amount);
            }
            if (pres.Count > 1)
            {
                exception = true;
                c.Add(LifecycleCodes.InvalidTransition, src, pres[1].SourceRowId,
                    $"Card transaction {id} has {pres.Count} presentments.", refs, pres[1].Amount);
            }
            var reversal = ev.FirstOrDefault(e => e.EventType == CardEventType.Reversal);
            if (reversal is not null && pres.Count > 0 && reversal.EventTimestamp >= pres[0].EventTimestamp)
            {
                exception = true;
                c.Add(LifecycleCodes.InvalidTransition, src, reversal.SourceRowId,
                    $"Card transaction {id}: reversal after presentment is not allowed (use refund or chargeback).",
                    refs, reversal.Amount);
            }
            if (reversal is not null && pres.Any(p => p.EventTimestamp > reversal.EventTimestamp))
            {
                exception = true;
                c.Add(LifecycleCodes.InvalidTransition, src, reversal.SourceRowId,
                    $"Card transaction {id}: presentment after reversal.", refs, reversal.Amount);
            }
            if (auth is not null && ev.Any(e => e.EventType != CardEventType.Authorization && e.EventTimestamp < auth.EventTimestamp))
            {
                exception = true;
                c.Add(LifecycleCodes.InvalidTransition, src, auth.SourceRowId,
                    $"Card transaction {id}: events precede the authorisation.", refs, auth.Amount);
            }

            foreach (var e in ev.Where(e => e.EventType is CardEventType.Refund or CardEventType.Chargeback or CardEventType.Reversal))
            {
                var orig = e.OriginalTransactionId;
                if (string.IsNullOrWhiteSpace(orig) || !journeys.ContainsKey(orig))
                {
                    exception = true;
                    c.Add(FindingCodes.MissingOriginalTransaction, src, e.SourceRowId,
                        $"{e.EventType} for {id} references unknown original card transaction {orig ?? "(none)"}.",
                        [R(src, e.SourceRowId)], e.Amount);
                }
                else if (e.EventType != CardEventType.Reversal
                         && !journeys[orig].Any(o => o.EventType == CardEventType.Presentment))
                {
                    exception = true;
                    c.Add(FindingCodes.InvalidState, src, e.SourceRowId,
                        $"{e.EventType} for {id} references {orig}, which was never presented.",
                        [R(src, e.SourceRowId)], e.Amount);
                }
            }

            // Money-moving legs need a known settlement that reached the bank.
            var legs = ev.Where(e => e.EventType is CardEventType.Presentment or CardEventType.Refund or CardEventType.Chargeback).ToList();
            var stage = ev[^1].EventType.ToString();
            foreach (var p in legs)
            {
                var pAge = c.Age(Day(p.EventTimestamp));
                var pOver = pAge > c.O.CardPresentmentToSettlementBusinessDays;
                if (string.IsNullOrWhiteSpace(p.SettlementId))
                {
                    if (!pOver) { pending = true; next = "Settlement batch"; timing = LifecycleTiming.Within; }
                    else
                    {
                        exception = true; timing = LifecycleTiming.Overdue;
                        c.Add(FindingCodes.MissingSettlement, src, p.SourceRowId,
                            $"{p.EventType} {p.CardEventId} has no settlement batch after {pAge} business days.",
                            [R(src, p.SourceRowId)], p.Amount);
                    }
                    continue;
                }
                if (!settlements.TryGetValue(p.SettlementId, out var s))
                {
                    exception = true;
                    c.Add(FindingCodes.MissingSettlement, src, p.SourceRowId,
                        $"{p.EventType} {p.CardEventId} references unknown settlement {p.SettlementId}.",
                        [R(src, p.SourceRowId)], p.Amount);
                    continue;
                }
                var sRef = R(SourceNames.CardSettlements, s.SourceRowId);
                refs.Add(sRef);
                stage = "Settlement batch";
                if (s.SourceStatus == SettlementStatus.Failed)
                {
                    exception = true;
                    c.Add(FindingCodes.InvalidState, SourceNames.CardSettlements, s.SourceRowId,
                        $"Settlement {s.SettlementId} for {p.CardEventId} failed.", [R(src, p.SourceRowId), sRef], s.GrossAmount);
                    continue;
                }
                var sAge = c.Age(s.SettlementDate);
                var sOver = sAge > c.O.CardSettlementToBankBusinessDays;
                var credit = bank[s.SettlementId].Where(b => b.DebitCredit == DebitCredit.Credit
                    && b.Amount == s.NetAmount).ToList();
                if (s.SourceStatus == SettlementStatus.Settled && credit.Count > 0)
                {
                    stage = "Settled to bank";
                    refs.AddRange(credit.Select(b => R(SourceNames.BankingTransactions, b.ExternalTransactionId)));
                    timing = LifecycleTiming.Within;
                }
                else if (!sOver) { pending = true; next = "Bank credit"; timing = LifecycleTiming.Within; }
                else
                {
                    exception = true; timing = LifecycleTiming.Overdue;
                    c.Add(LifecycleCodes.MissingExternalOutcome, SourceNames.CardSettlements, s.SourceRowId,
                        $"Settlement {s.SettlementId} ({s.NetAmount}) has no matching bank credit {sAge} business days after {s.SettlementDate:yyyy-MM-dd}.",
                        [sRef], s.NetAmount);
                }
            }

            // Authorisation never presented or reversed.
            if (legs.Count == 0 && reversal is null && auth is not null)
            {
                if (age > c.O.CardAuthorizationToPresentmentBusinessDays)
                {
                    exception = true; timing = LifecycleTiming.Overdue;
                    c.Add(LifecycleCodes.Stalled, src, auth.SourceRowId,
                        $"Card authorisation {id} has no presentment after {age} business days.", refs, auth.Amount, timing: true);
                }
                else { pending = true; next = "Presentment"; timing = LifecycleTiming.Within; }
            }
            if (reversal is not null && legs.Count == 0) stage = "Reversal";

            var state = exception ? LifecycleState.Exception : pending ? LifecycleState.Pending : LifecycleState.Completed;
            Verdict(c, LifecycleKind.Card, id, stage, state == LifecycleState.Pending ? next : null, state, timing, age, refs);
        }
    }
}
