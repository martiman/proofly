namespace Proofly.Data.Matching;

public sealed record RunScope(string RunId, string LegalEntityId);

public sealed record MatchMoney(decimal Amount, string Currency);

/// <summary>One record on a match group. Side is LEFT (operational: transfer, direct debit, card) or RIGHT (evidence: bank, ledger).</summary>
public sealed record MatchCandidateRow(
    string TransactionId, string Side, string Kind, string Label, MatchMoney Amount, DateTimeOffset Timestamp,
    string Reference, bool Selected, decimal? Score);

public sealed record MatchApprovalRow(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion,
    string ProposerId, string ProposerName, string? ApproverId, string? ApproverName,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed record MatchGroupRow(
    string Id, string Status, bool Manual, MatchMoney? Difference, string RuleVersion, string Tolerance, string Cardinality,
    IReadOnlyList<MatchCandidateRow> Left, IReadOnlyList<MatchCandidateRow> Right,
    IReadOnlyList<string> ExceptionIds, MatchApprovalRow? Approval);

public sealed record CurrencyUnmatched(string Currency, long Count, decimal Value);

/// <summary>Header statistics. AutoMatchRate = automatically matched groups / all groups (0..1, null when the run has no groups).</summary>
public sealed record MatchingStats(decimal? AutoMatchRate, long TotalGroups, long OpenGroups, long UnmatchedCount, IReadOnlyList<CurrencyUnmatched> UnmatchedByCurrency);

public sealed record WorkbenchRow(string FocusTransactionId, IReadOnlyList<MatchGroupRow> Groups, IReadOnlyList<MatchCandidateRow> Unmatched, MatchingStats Stats);

/// <summary>Why a workbench focus could not be resolved.</summary>
public enum FocusFailure { None, InvalidTransactionId, NotFound }
