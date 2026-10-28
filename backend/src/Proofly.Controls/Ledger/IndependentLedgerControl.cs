using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Ledger;

/// <summary>Pure, deterministic independent recalculation of client-account balances (docs/controls/independent-ledger.md).</summary>
public static class IndependentLedgerControl
{
    public static ControlResult Evaluate(LedgerInput input, LedgerOptions? options = null) => Calculate(input, options).Result;

    public static LedgerEvaluation Calculate(LedgerInput input, LedgerOptions? options = null)
    {
        options ??= new LedgerOptions();
        var findings = new List<Finding>();
        var ord = StringComparer.Ordinal;
        var rv = options.RuleVersion;

        Finding F(string key, string code, Severity sev, string desc, IEnumerable<RecordRef> recs, Money? value = null, bool blocking = false) =>
            new($"ledger:{code}:{key}", ControlArea.IndependentLedger, code, sev, desc,
                recs.Distinct().OrderBy(r => r.SourceName, ord).ThenBy(r => r.SourceRowId, ord).ToList(), value, rv, blocking);

        var openings = input.OpeningBalances.GroupBy(o => (o.AccountId, o.Amount.Currency))
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Amount.Amount).First());
        var recorded = input.RecordedBalances
            .Where(b => b.BalanceDate == input.BusinessDate && b.BalanceType == AccountBalanceType.DailyRollforward)
            .GroupBy(b => (b.AccountId, b.Balance.Currency)).ToDictionary(g => g.Key, g => g.First());
        var scope = openings.Keys.Concat(recorded.Keys).ToHashSet();
        var transfers = input.Transfers.GroupBy(t => t.InstructionId, ord).ToDictionary(g => g.Key, g => g.First(), ord);

        var contribs = scope.ToDictionary(k => k, _ => new List<Contribution>());
        var exclusions = scope.ToDictionary(k => k, _ => new List<Exclusion>());
        var refsByRef = new HashSet<string>(ord);
        // Per-account lookups built once (a scan of every entry per account would be quadratic).
        var entriesByAccount = input.Entries.ToLookup(e => (e.AccountId, e.Amount.Currency));
        var scopeAccounts = scope.Select(k => k.AccountId).ToHashSet(ord);
        static RecordRef Ref(LedgerEntry e) => new(SourceNames.InternalLedgerEntries, e.LedgerEntryId);

        // Bank transactions are supporting evidence, linked through the bank reference.
        var bankByRef = input.BankTransactions.Where(b => b.BankReference is not null)
            .GroupBy(b => b.BankReference!, ord).ToDictionary(g => g.Key, g => g.OrderBy(b => b.ExternalTransactionId, ord).First(), ord);
        IEnumerable<RecordRef> Support(LedgerEntry e)
        {
            if (e.TransactionReference is null) yield break;
            if (transfers.TryGetValue(e.TransactionReference, out var t))
            {
                yield return new RecordRef(SourceNames.TransferInstructions, t.SourceRowId);
                if (t.ExternalReference is not null && bankByRef.TryGetValue(t.ExternalReference, out var b1))
                    yield return new RecordRef(SourceNames.BankingTransactions, b1.SourceRowId);
            }
            if (bankByRef.TryGetValue(e.TransactionReference, out var b2))
                yield return new RecordRef(SourceNames.BankingTransactions, b2.SourceRowId);
        }

        var seenIds = new Dictionary<string, int>(ord);
        var seenSemantic = new HashSet<string>(ord);
        var referenced = new HashSet<string>(ord);

        foreach (var e in input.Entries.OrderBy(e => e.BookingTimestamp).ThenBy(e => e.LedgerEntryId, ord)
                     .ThenBy(e => e.Amount.Amount).ThenBy(e => e.AccountId, ord))
        {
            var key = (e.AccountId, e.Amount.Currency);
            var rf = Ref(e);
            var support = Support(e).ToList();
            if (e.TransactionReference is not null) referenced.Add(e.TransactionReference);
            var posted = e.SourceStatus == LedgerEntryStatus.Posted;
            var signed = Signed(e.Amount, e.DebitCredit);
            var recordedEffect = posted ? signed : Money.Zero(e.Amount.Currency);

            if (!scope.Contains(key))
            {
                var otherCcy = scopeAccounts.Contains(e.AccountId);
                findings.Add(otherCcy
                    ? F($"{rf.SourceName}:{e.LedgerEntryId}", LedgerCodes.CalculationScopeMismatch, Severity.High,
                        $"Ledger entry {e.LedgerEntryId} is in {e.Amount.Currency} but account {e.AccountId} is calculated only in other currencies; it was not included and never converted.", [rf], e.Amount)
                    : F($"{rf.SourceName}:{e.LedgerEntryId}", LedgerCodes.MovementUnmapped, Severity.High,
                        $"Ledger entry {e.LedgerEntryId} belongs to account {e.AccountId} ({e.Amount.Currency}), which has no opening or recorded balance in scope; it was not included.", [rf], e.Amount));
                continue;
            }
            var ex = exclusions[key];
            var list = contribs[key];

            if (!posted)
            {
                ex.Add(new Exclusion(rf, $"NOT_POSTED:{WireEnum.ToWire(e.SourceStatus)}"));
                continue;
            }
            if (e.Amount.Amount <= 0m)
            {
                ex.Add(new Exclusion(rf, "NON_POSITIVE_AMOUNT"));
                findings.Add(F($"{rf.SourceName}:{e.LedgerEntryId}", FindingCodes.InvalidSign, Severity.High,
                    $"Ledger entry {e.LedgerEntryId} has a non-positive amount {e.Amount}; amounts are magnitudes and direction is the debit/credit indicator. Excluded.", [rf, ..support], e.Amount));
                list.Add(new Contribution(rf, ContributionKind.Applied, e.DebitCredit, e.Amount, Money.Zero(e.Amount.Currency), recordedEffect));
                continue;
            }

            // duplicates: same id, or same account/reference/type/direction/amount
            var occurrence = seenIds.TryGetValue(e.LedgerEntryId, out var n) ? n + 1 : 1;
            seenIds[e.LedgerEntryId] = occurrence;
            var semantic = e.TransactionReference is null ? null
                : $"{e.AccountId}|{e.Amount.Currency}|{e.TransactionReference}|{e.EntryType}|{e.DebitCredit}|{e.Amount.Amount}";
            if (occurrence > 1 || (semantic is not null && !seenSemantic.Add(semantic)))
            {
                ex.Add(new Exclusion(rf, "DUPLICATE"));
                findings.Add(F($"{rf.SourceName}:{e.LedgerEntryId}:{occurrence}:{semantic ?? "-"}", FindingCodes.DuplicateRecord, Severity.High,
                    $"Ledger entry {e.LedgerEntryId} ({e.Amount}) duplicates an earlier movement for account {e.AccountId}; it is included once.", [rf, ..support], e.Amount));
                list.Add(new Contribution(rf, ContributionKind.Applied, e.DebitCredit, e.Amount, Money.Zero(e.Amount.Currency), recordedEffect));
                continue;
            }
            if (e.ValueDate > input.BusinessDate)
            {
                ex.Add(new Exclusion(rf, "VALUE_DATE_AFTER_BUSINESS_DATE"));
                findings.Add(F($"{rf.SourceName}:{e.LedgerEntryId}", LedgerCodes.InvalidValueDate, Severity.Medium,
                    $"Ledger entry {e.LedgerEntryId} has value date {e.ValueDate:yyyy-MM-dd} after the business date {input.BusinessDate:yyyy-MM-dd}; excluded from the independent balance.", [rf], e.Amount));
                list.Add(new Contribution(rf, ContributionKind.Applied, e.DebitCredit, e.Amount, Money.Zero(e.Amount.Currency), recordedEffect));
                continue;
            }

            var direction = e.DebitCredit;
            var kind = ContributionKind.Applied;
            if (e.TransactionReference is not null && transfers.TryGetValue(e.TransactionReference, out var t) && e.EntryType == LedgerEntryType.Transfer)
            {
                if (t.SourceStatus is TransferStatus.Rejected or TransferStatus.Cancelled)
                {
                    ex.Add(new Exclusion(rf, $"TRANSFER_{WireEnum.ToWire(t.SourceStatus)}"));
                    list.Add(new Contribution(rf, ContributionKind.Applied, e.DebitCredit, e.Amount, Money.Zero(e.Amount.Currency), recordedEffect));
                    continue;
                }
                if (e.DebitCredit != DebitCredit.Debit)
                {
                    direction = DebitCredit.Debit;
                    kind = ContributionKind.SignCorrected;
                    findings.Add(F($"{rf.SourceName}:{e.LedgerEntryId}", FindingCodes.InvalidSign, Severity.High,
                        $"Outgoing transfer {t.InstructionId} is posted as CREDIT in ledger entry {e.LedgerEntryId}; the independent balance applies it as DEBIT ({e.Amount}).", [rf, ..support], e.Amount));
                }
            }
            list.Add(new Contribution(rf, kind, direction, e.Amount, Signed(e.Amount, direction), recordedEffect));
        }

        // Settled transfers with no ledger entry at all: movement missing from the primary ledger.
        foreach (var t in input.Transfers.OrderBy(t => t.InstructionId, ord).ThenBy(t => t.SourceRowId, ord))
        {
            var key = (t.ClientAccountId, t.Amount.Currency);
            if (t.SourceStatus != TransferStatus.Settled || referenced.Contains(t.InstructionId) || !scope.Contains(key)) continue;
            if (t.RequestedValueDate is { } vd && vd > input.BusinessDate) continue;
            var rf = new RecordRef(SourceNames.TransferInstructions, t.SourceRowId);
            var support = new List<RecordRef> { rf };
            if (t.ExternalReference is not null && bankByRef.TryGetValue(t.ExternalReference, out var b))
                support.Add(new RecordRef(SourceNames.BankingTransactions, b.SourceRowId));
            contribs[key].Add(new Contribution(rf, ContributionKind.MissingFromLedger, DebitCredit.Debit, t.Amount,
                Signed(t.Amount, DebitCredit.Debit), Money.Zero(t.Amount.Currency)));
            findings.Add(F($"{rf.SourceName}:{t.SourceRowId}", LedgerCodes.MovementMissing, Severity.High,
                $"Settled transfer {t.InstructionId} ({t.Amount}) on account {t.ClientAccountId} has no ledger movement; it is included in the independent balance as a debit.", support, t.Amount));
        }

        var balances = new List<IndependentBalance>();
        foreach (var key in scope.OrderBy(k => k.AccountId, ord).ThenBy(k => k.Item2, ord))
        {
            var ccy = key.Item2;
            var zero = Money.Zero(ccy);
            var cs = contribs[key].OrderBy(c => c.Record.SourceName, ord).ThenBy(c => c.Record.SourceRowId, ord).ThenBy(c => c.Kind).ToList();
            var applied = cs.Where(c => c.IndependentEffect.Amount != 0m).ToList();
            Money Sum(Func<Contribution, bool> p) => applied.Where(p).Aggregate(zero, (a, c) => a + c.Amount);
            var entryTypes = entriesByAccount[(key.AccountId, ccy)]
                .GroupBy(e => e.LedgerEntryId, ord).ToDictionary(g => g.Key, g => g.First().EntryType, ord);
            bool IsAdj(Contribution c) => c.Record.SourceName == SourceNames.InternalLedgerEntries
                && entryTypes.TryGetValue(c.Record.SourceRowId, out var ty) && ty == LedgerEntryType.Adjustment;
            var credits = Sum(c => c.Direction == DebitCredit.Credit && !IsAdj(c));
            var debits = Sum(c => c.Direction == DebitCredit.Debit && !IsAdj(c));
            var adjustments = applied.Where(IsAdj).Aggregate(zero, (a, c) => a + c.IndependentEffect);

            openings.TryGetValue(key, out var op);
            recorded.TryGetValue(key, out var rec);
            var recRef = rec is null ? null : new RecordRef(SourceNames.ClientAccountBalances, rec.SourceRowId);
            var opRef = new RecordRef(LedgerCodes.OpeningSource, key.AccountId);
            var where = $"{key.AccountId}:{ccy}";

            if (op is null)
            {
                findings.Add(F(where, LedgerCodes.OpeningBalanceMissing, Severity.High,
                    $"No opening balance for account {key.AccountId} in {ccy}; no closing-balance comparison was produced.",
                    recRef is null ? [opRef] : [recRef], null, true));
                balances.Add(new IndependentBalance(key.AccountId, ccy, null, false, credits, debits, adjustments, null, rec?.Balance, null, null, cs, exclusions[key]));
                continue;
            }
            var supported = !string.IsNullOrWhiteSpace(op.SupportReference);
            if (!supported)
                findings.Add(F(where, LedgerCodes.OpeningBalanceUnsupported, Severity.High,
                    $"Opening balance {op.Amount} for account {key.AccountId} has no supporting evidence reference; the independent balance is calculated but not supported.",
                    [opRef], op.Amount, true));

            var independent = op.Amount + cs.Aggregate(zero, (a, c) => a + c.IndependentEffect);
            if (rec is null)
            {
                findings.Add(F(where, LedgerCodes.RecordedBalanceMissing, Severity.High,
                    $"No recorded closing balance for account {key.AccountId} in {ccy} dated {input.BusinessDate:yyyy-MM-dd}; no comparison was possible.",
                    [opRef], independent, true));
                balances.Add(new IndependentBalance(key.AccountId, ccy, op.Amount, supported, credits, debits, adjustments, independent, null, null, null, cs, exclusions[key]));
                continue;
            }

            var diff = independent - rec.Balance;
            var variance = cs.Aggregate(zero, (a, c) => a + c.Variance);
            var residual = diff - variance;
            balances.Add(new IndependentBalance(key.AccountId, ccy, op.Amount, supported, credits, debits, adjustments, independent, rec.Balance, diff, residual, cs, exclusions[key]));
            if (Math.Abs(diff.Amount) > options.Tolerance)
            {
                var drivers = cs.Where(c => c.Variance.Amount != 0m).ToList();
                var text = drivers.Count == 0 ? "no contributing record explains it"
                    : string.Join("; ", drivers.Select(c => $"{c.Record.SourceName}/{c.Record.SourceRowId} {c.Kind} {c.Variance}"));
                findings.Add(F(where, FindingCodes.BalanceDifference, Severity.High,
                    $"Account {key.AccountId} ({ccy}): independent {independent} differs from recorded {rec.Balance} by {diff}. Contributing records: {text}. Unexplained residual: {residual}.",
                    drivers.Select(c => c.Record).Append(recRef!), diff, options.BalanceDifferenceBlocking));
            }
        }

        return new LedgerEvaluation(balances, ControlResult.From(ControlArea.IndependentLedger, rv, findings));
    }

    private static Money Signed(Money m, DebitCredit d) => d == DebitCredit.Credit ? m : m.Negate();
}
