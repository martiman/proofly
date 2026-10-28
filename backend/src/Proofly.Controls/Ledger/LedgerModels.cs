using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Controls.Ledger;

/// <summary>Reason codes owned by the independent ledger control (others come from FindingCodes).</summary>
public static class LedgerCodes
{
    public const string OpeningBalanceMissing = "OPENING_BALANCE_MISSING";
    public const string OpeningBalanceUnsupported = "OPENING_BALANCE_UNSUPPORTED";
    public const string MovementUnmapped = "MOVEMENT_UNMAPPED";
    public const string MovementMissing = "MOVEMENT_MISSING";
    public const string InvalidValueDate = "INVALID_VALUE_DATE";
    public const string RecordedBalanceMissing = "RECORDED_BALANCE_MISSING";
    public const string CalculationScopeMismatch = "CALCULATION_SCOPE_MISMATCH";
    public const string OpeningSource = "opening_balances";
}

/// <summary>An independently supplied opening balance. <paramref name="SupportReference"/> is its provenance (null/blank = unsupported).</summary>
public sealed record OpeningBalanceEvidence(string AccountId, Money Amount, string? SupportReference);

/// <summary>Control input. Recorded balances are the internal closing balances (only those dated <see cref="BusinessDate"/> and of type DailyRollforward are compared).</summary>
public sealed record LedgerInput(
    DateOnly BusinessDate,
    IReadOnlyList<OpeningBalanceEvidence> OpeningBalances,
    IReadOnlyList<LedgerEntry> Entries,
    IReadOnlyList<BankTransaction> BankTransactions,
    IReadOnlyList<TransferInstruction> Transfers,
    IReadOnlyList<AccountBalance> RecordedBalances);

/// <summary>Immutable rule parameters. Every transfer instruction is an outgoing payment (expected ledger direction: debit).</summary>
public sealed record LedgerOptions
{
    public string RuleVersion { get; init; } = "independent-ledger/1";
    /// <summary>Absolute tolerance on |independent - recorded| for raising BALANCE_DIFFERENCE; the difference itself is always reported. Default 0.</summary>
    public decimal Tolerance { get; init; }
    public bool BalanceDifferenceBlocking { get; init; } = true;
}

public enum ContributionKind { Applied, SignCorrected, MissingFromLedger }

/// <summary>One record's effect on a position. Variance = IndependentEffect - RecordedEffect, where RecordedEffect is what the primary ledger booked as posted.</summary>
public sealed record Contribution(RecordRef Record, ContributionKind Kind, DebitCredit Direction, Money Amount, Money IndependentEffect, Money RecordedEffect)
{
    public Money Variance => IndependentEffect - RecordedEffect;
}

public sealed record Exclusion(RecordRef Record, string Reason);

/// <summary>Independent position of one account and currency; consumable by the safeguarding control.</summary>
public sealed record IndependentBalance(
    string AccountId, string Currency, Money? Opening, bool OpeningSupported, Money Credits, Money Debits, Money Adjustments,
    Money? Independent, Money? Recorded, Money? Difference, Money? UnexplainedResidual,
    IReadOnlyList<Contribution> Contributions, IReadOnlyList<Exclusion> Exclusions);

public sealed record LedgerEvaluation(IReadOnlyList<IndependentBalance> Balances, ControlResult Result);
