using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Controls.Lifecycle;

/// <summary>Reason codes of the lifecycle control that are not in the shared <c>FindingCodes</c>.</summary>
public static class LifecycleCodes
{
    public const string InvalidTransition = "INVALID_TRANSITION";
    public const string MissingInternalPosting = "MISSING_INTERNAL_POSTING";
    public const string MissingExternalOutcome = "MISSING_EXTERNAL_OUTCOME";
    public const string Stalled = "STALLED";
    public const string LateOutcome = "LATE_OUTCOME";
    public const string ReversalNotPosted = "REVERSAL_NOT_POSTED";
    public const string ConflictingOutcomes = "CONFLICTING_OUTCOMES";
}

public enum LifecycleKind { Transfer, DirectDebit, Card }

/// <summary>Pending = inside window, not an exception; Completed = supported terminal outcome; Exception = breach.</summary>
public enum LifecycleState { Pending, Completed, Exception }

public enum LifecycleTiming { Within, Overdue, NotApplicable }

/// <summary>Per-transaction verdict: stage reached, expected next stage (null when terminal) and timing.</summary>
public sealed record LifecycleVerdict(
    LifecycleKind Kind, string TransactionId, string StageReached, string? ExpectedNextStage,
    LifecycleState State, LifecycleTiming Timing, int AgeBusinessDays, IReadOnlyList<RecordRef> Records);

/// <summary>Input population. Everything is joined by ids; nothing depends on the matching engine.</summary>
public sealed record LifecycleInput(
    DateOnly BusinessDate,
    IReadOnlyList<TransferInstruction> Transfers,
    IReadOnlyList<DirectDebitEvent> DirectDebitEvents,
    IReadOnlyList<CardEvent> CardEvents,
    IReadOnlyList<CardSettlement> CardSettlements,
    IReadOnlyList<LedgerEntry> LedgerEntries,
    IReadOnlyList<BankTransaction> BankTransactions);

/// <summary>
/// Immutable rule parameters. Windows are in business days (Mon-Fri minus <see cref="Holidays"/>), counted from the
/// stage date to the run's business date; a transaction is overdue only when age is strictly greater than the window.
/// </summary>
public sealed record LifecycleOptions
{
    public string RuleVersion { get; init; } = "lifecycle-1.0";
    /// <summary>Instruction to executed (ledger posting and bank evidence).</summary>
    public int TransferWindowBusinessDays { get; init; } = 1;
    /// <summary>Collection date to settled/returned outcome.</summary>
    public int DirectDebitWindowBusinessDays { get; init; } = 3;
    /// <summary>Authorisation to presentment.</summary>
    public int CardAuthorizationToPresentmentBusinessDays { get; init; } = 7;
    /// <summary>Presentment to a known processor settlement id.</summary>
    public int CardPresentmentToSettlementBusinessDays { get; init; } = 3;
    /// <summary>Processor settlement date to bank credit.</summary>
    public int CardSettlementToBankBusinessDays { get; init; } = 2;
    public bool RequireCardAuthorization { get; init; } = true;
    public IReadOnlySet<DateOnly> Holidays { get; init; } = new HashSet<DateOnly>();
    public Severity DefaultSeverity { get; init; } = Severity.High;
    public Severity TimingSeverity { get; init; } = Severity.Medium;
    /// <summary>Reason codes whose findings block the control.</summary>
    public IReadOnlySet<string> BlockingCodes { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FindingCodes.ReturnNotPosted, LifecycleCodes.ReversalNotPosted, LifecycleCodes.ConflictingOutcomes,
        FindingCodes.InvalidState,
    };
}

public sealed record LifecycleResult(ControlResult Control, IReadOnlyList<LifecycleVerdict> Verdicts);
