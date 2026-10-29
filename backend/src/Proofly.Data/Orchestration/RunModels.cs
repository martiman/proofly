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

public static class RunJobs
{
    /// <summary>Job type of the run processing handler.</summary>
    public const string ProcessRun = "run.process";
}

/// <param name="LegalEntityId">Entity of the run.</param>
/// <param name="BusinessDate">Business date of the run.</param>
/// <param name="RunId">The run to process (set by a reprocess request). Null = the current run of the entity and date (created when absent).</param>
/// <param name="Trigger"><c>evidence</c> (delivery ingested) or <c>reprocess</c>.</param>
public sealed record ProcessRunPayload(string LegalEntityId, DateOnly BusinessDate, string? RunId = null, string Trigger = "evidence");

/// <summary>
/// Rule parameters of one ruleset. The ruleset version is derived from the rule versions of the five controls: change a
/// parameter only together with its RuleVersion, because the version is what the run records and the hash covers.
/// </summary>
public sealed record RunProcessingOptions
{
    public CompletenessOptions Completeness { get; init; } = new();
    public LedgerOptions Ledger { get; init; } = new();
    public MatchingOptions Matching { get; init; } = new();
    public LifecycleOptions Lifecycle { get; init; } = new();
    public SafeguardingOptions Safeguarding { get; init; } = new();

    /// <summary>Calendar days after the business date by which an exception is due, per severity.</summary>
    public int DueDays(Severity s) => s switch { Severity.Critical => 1, Severity.High => 2, Severity.Medium => 3, _ => 5 };

    public string RulesetVersion =>
        $"proofly-controls/1[{Completeness.RuleVersion},{Ledger.RuleVersion},{Matching.RuleVersion},{Lifecycle.RuleVersion},{Safeguarding.RuleVersion}]";
}

/// <summary>An accepted canonical record as stored, with its table identity for links and match candidates.</summary>
public sealed record LoadedRecord(string Table, long DbId, string DeliveryId, string SourceName, ISourceRecord Record, int? RowNumber);

public sealed record EvidenceDelivery(DeliveryInfo Info, string FileName);

/// <summary>Everything the pure evaluator needs. <see cref="RunId"/> only seeds exception/group ids; it never enters the result hash.</summary>
public sealed record RunInputs(
    string RunId, string LegalEntityId, DateOnly BusinessDate, IReadOnlyList<string> MandatorySources,
    IReadOnlyList<EvidenceDelivery> Deliveries, IReadOnlyList<RejectedRecord> Rejections,
    IReadOnlyList<LoadedRecord> Records, IReadOnlyList<AdjustmentInput> Adjustments);

public sealed record ControlOutcome(
    ControlArea Area, ControlState State, string RuleVersion, int RecordCount, int FindingCount, int BlockingCount);

public sealed record RecordLink(string Table, long DbId);

public sealed record ExceptionDraft(
    string Id, Finding Finding, string Title, string Description, DateTimeOffset DueAt, string? DeliveryId, string? SourceFile, string? SourceRowId,
    IReadOnlyList<RecordLink> Links, string RuleId, string? Tolerance, string ExpectedEffect, string CalculationJson);

public sealed record MatchCandidateDraft(string Kind, string Side, string Table, long DbId, Money Amount, bool Selected);

public sealed record MatchGroupDraft(
    string Id, MatchGroup Group, string ToleranceText, IReadOnlyList<MatchCandidateDraft> Candidates, IReadOnlyList<string> ExceptionIds);

public sealed record PositionDraft(CurrencyPosition Position, string DetailJson);

/// <summary>Pure outcome of the five controls. <see cref="ResultHash"/> covers the controls' results only (no ids, no workflow state).</summary>
public sealed record RunEvaluation(
    string RulesetVersion, IReadOnlyList<ControlOutcome> Controls, IReadOnlyList<ExceptionDraft> Exceptions,
    IReadOnlyList<MatchGroupDraft> Groups, IReadOnlyList<IndependentBalance> Balances, IReadOnlyList<PositionDraft> Positions,
    ControlState CompletenessState, string ResultHash);

public enum RunProcessingStatus { Processed, AlreadyProcessed, RunSuperseded, NotFound }

public sealed record RunProcessingOutcome(
    RunProcessingStatus Status, string? RunId, string? ResultHash, string? State, int ExceptionCount, int CarriedOverCount);

internal static class RecordKinds
{
    public static (string Table, string Source) Of(ISourceRecord r) => r switch
    {
        TransferInstruction => ("transfer_instructions", SourceNames.TransferInstructions),
        DirectDebitEvent => ("direct_debit_events", SourceNames.DirectDebitEvents),
        CardEvent => ("card_events", SourceNames.CardTransactions),
        CardSettlement => ("card_settlements", SourceNames.CardSettlements),
        LedgerEntry => ("ledger_entries", SourceNames.InternalLedgerEntries),
        AccountBalance => ("account_balances", SourceNames.ClientAccountBalances),
        BankTransaction => ("bank_transactions", SourceNames.BankingTransactions),
        SafeguardingBalance => ("safeguarding_balances", SourceNames.SafeguardingBalances),
        _ => throw new InvalidOperationException($"Unsupported record type {r.GetType().Name}."),
    };

    public static string CandidateKind(string source) => source switch
    {
        SourceNames.TransferInstructions => "BANK_TRANSFER",
        SourceNames.DirectDebitEvents => "DIRECT_DEBIT",
        SourceNames.CardTransactions or SourceNames.CardSettlements => "CARD",
        SourceNames.BankingTransactions => "BANK_EVIDENCE",
        SourceNames.InternalLedgerEntries => "LEDGER_ENTRY",
        _ => throw new InvalidOperationException($"No match candidate kind for source {source}."),
    };
}
