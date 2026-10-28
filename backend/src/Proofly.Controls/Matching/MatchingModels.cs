using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Controls.Matching;

public enum MatchGroupStatus { Matched, Unmatched, ValueMismatch }

/// <summary>Reason / rule codes of the matching control (docs/controls/matching.md). Shared codes live in FindingCodes.</summary>
public static class MatchingCodes
{
    public const string ExactIdentifierMatch = "EXACT_IDENTIFIER_MATCH";
    public const string ExactCompositeMatch = "EXACT_COMPOSITE_MATCH";
    public const string DateToleranceMatch = "DATE_TOLERANCE_MATCH";
    public const string AmountToleranceMatch = "AMOUNT_TOLERANCE_MATCH";
    public const string GroupedMatch = "GROUPED_MATCH";
    public const string NoCandidate = "NO_CANDIDATE";
    public const string AmbiguousCandidates = "AMBIGUOUS_CANDIDATES";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string PartialMatch = "PARTIAL_MATCH";
}

/// <summary>One record taking part in matching, normalised from a canonical record. Date null = not comparable.</summary>
public sealed record MatchItem(string Source, string Id, string? Reference, Money Amount, DateOnly? Date)
{
    // Computed once: the matching engine and the orchestration look it up many times per record.
    public string Key { get; } = Source + "/" + Id;
    public RecordRef Ref => new(Source, Id);
}

/// <summary>A reconciliation pair: internal-side records (left) against counterpart records (right). Each pair has its own record-use scope.</summary>
public sealed record MatchPair(string Name, IReadOnlyList<MatchItem> Left, IReadOnlyList<MatchItem> Right);

public sealed record MatchingInput(IReadOnlyList<MatchPair> Pairs)
{
    public const string InstructionToBank = "instruction_to_bank";
    public const string LedgerToBank = "ledger_to_bank";
    public const string DirectDebitToBank = "direct_debit_to_bank";
    public const string SettlementToBank = "card_settlement_to_bank";
    public const string PresentmentToSettlement = "card_presentment_to_settlement";

    /// <summary>
    /// Builds the standard pairs from canonical records. Empty pairs are omitted. Instruction/direct debit reference is
    /// ExternalReference / InstructionId; ledger reference is TransactionReference; settlements match on net amount
    /// to the bank and presentments (gross, by SettlementId) to the settlement gross amount.
    /// </summary>
    public static MatchingInput FromRecords(
        IEnumerable<TransferInstruction> instructions, IEnumerable<LedgerEntry> ledger,
        IEnumerable<DirectDebitEvent> directDebits, IEnumerable<CardSettlement> settlements,
        IEnumerable<CardEvent> cardEvents, IEnumerable<BankTransaction> bank)
    {
        var banks = bank.Select(b => new MatchItem(SourceNames.BankingTransactions, b.ExternalTransactionId, b.BankReference, b.Amount, b.ValueDate)).ToList();
        var sl = settlements.ToList();
        var pairs = new List<MatchPair>
        {
            new(InstructionToBank,
                instructions.Select(i => new MatchItem(SourceNames.TransferInstructions, i.SourceRowId, i.ExternalReference, i.Amount, i.RequestedValueDate)).ToList(), banks),
            new(LedgerToBank,
                ledger.Select(l => new MatchItem(SourceNames.InternalLedgerEntries, l.LedgerEntryId, l.TransactionReference, l.Amount, l.ValueDate)).ToList(), banks),
            new(DirectDebitToBank,
                directDebits.Select(d => new MatchItem(SourceNames.DirectDebitEvents, d.SourceRowId, d.InstructionId, d.Amount, d.CollectionDate)).ToList(), banks),
            new(SettlementToBank,
                sl.Select(s => new MatchItem(SourceNames.CardSettlements, s.SourceRowId, s.SettlementId, s.NetAmount, s.SettlementDate)).ToList(), banks),
            new(PresentmentToSettlement,
                cardEvents.Where(e => e.EventType == CardEventType.Presentment && !string.IsNullOrEmpty(e.SettlementId))
                    .Select(e => new MatchItem(SourceNames.CardTransactions, e.SourceRowId, e.SettlementId, e.Amount, DateOnly.FromDateTime(e.EventTimestamp.UtcDateTime))).ToList(),
                sl.Select(s => new MatchItem(SourceNames.CardSettlements, s.SourceRowId, s.SettlementId, s.GrossAmount, s.SettlementDate)).ToList()),
        };
        return new MatchingInput(pairs.Where(p => p.Left.Count > 0 || p.Right.Count > 0).ToList());
    }
}

/// <summary>Immutable rule parameters. Per-currency maps fall back to the defaults.</summary>
public sealed record MatchingOptions
{
    public string RuleVersion { get; init; } = "matching-1.0";

    /// <summary>Allowed amount difference per currency for AMOUNT_TOLERANCE_MATCH and for grouped sums. Default 0 (no tolerance).</summary>
    public IReadOnlyDictionary<string, decimal> AmountTolerance { get; init; } = new Dictionary<string, decimal>();
    public decimal DefaultAmountTolerance { get; init; }

    /// <summary>Allowed value-date distance in days per currency for tolerant rules. Default 2.</summary>
    public IReadOnlyDictionary<string, int> DateWindowDays { get; init; } = new Dictionary<string, int>();
    public int DefaultDateWindowDays { get; init; } = 2;

    /// <summary>Whether controlled one-to-many / many-to-one grouping is permitted.</summary>
    public bool AllowGroupedMatches { get; init; } = true;

    public Proofly.Domain.Enums.Severity ValueMismatchSeverity { get; init; } = Proofly.Domain.Enums.Severity.Medium;
    public Proofly.Domain.Enums.Severity UnmatchedSeverity { get; init; } = Proofly.Domain.Enums.Severity.Medium;
    public Proofly.Domain.Enums.Severity AmbiguousSeverity { get; init; } = Proofly.Domain.Enums.Severity.Medium;

    public decimal ToleranceFor(string ccy) => AmountTolerance.TryGetValue(ccy, out var t) ? t : DefaultAmountTolerance;
    public int WindowFor(string ccy) => DateWindowDays.TryGetValue(ccy, out var d) ? d : DefaultDateWindowDays;
}

/// <summary>Result of matching one set of records. Cardinality is "1:1", "1:N", "N:1", "1:0" (left unmatched) or "0:1".</summary>
public sealed record MatchGroup(
    string Id, string Pair, MatchGroupStatus Status, string Cardinality, string RuleId, string RuleVersion,
    string ReasonCode, Money? ToleranceUsed, int DateWindowDaysUsed, Money? ValueDifference, int? DateDifferenceDays,
    string Rationale, IReadOnlyList<RecordRef> Left, IReadOnlyList<RecordRef> Right, IReadOnlyList<RecordRef> Candidates);

public sealed record MatchingResult(ControlResult Control, IReadOnlyList<MatchGroup> Groups);
