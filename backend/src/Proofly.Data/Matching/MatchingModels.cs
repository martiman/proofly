namespace Proofly.Data.Matching;

public sealed record RunScope(string RunId, string LegalEntityId, string State = "");

public sealed record MatchMoney(decimal Amount, string Currency);

/// <summary>One record on a match group. Side is LEFT (operational: transfer, direct debit, card) or RIGHT (evidence: bank, ledger).</summary>
public sealed record MatchCandidateRow(
    string TransactionId, string Side, string Kind, string Label, MatchMoney Amount, DateTimeOffset? Timestamp,
    string? Reference, bool Selected, decimal? Score, string GroupId = "", long CandidateId = 0);

public sealed record MatchApprovalRow(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion,
    string ProposerId, string ProposerName, string? ApproverId, string? ApproverName,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed record MatchGroupRow(
    string Id, string Status, bool Manual, MatchMoney? Difference, string? RuleVersion, string? Tolerance, string? Rationale, string Cardinality,
    IReadOnlyList<MatchCandidateRow> Left, IReadOnlyList<MatchCandidateRow> Right,
    IReadOnlyList<string> ExceptionIds, MatchApprovalRow? Approval, int Version = 0);

/// <summary>Filter of the unmatched population. Pair = candidate kind (TransactionKind); Search = reference, row key, source name or an exact transaction id.</summary>
public sealed record UnmatchedFilter(string? Pair = null, string? Currency = null, string? Search = null);

/// <summary>Keyset position of the unmatched list (order: match group id, candidate id).</summary>
public sealed record UnmatchedCursorState(string GroupId, long CandidateId);

public sealed record CurrencyUnmatched(string Currency, long Count, decimal Value);

/// <summary>Header statistics. AutoMatchRate = automatically matched groups / all groups (0..1, null when the run has no groups).</summary>
public sealed record MatchingStats(decimal? AutoMatchRate, long TotalGroups, long OpenGroups, long UnmatchedCount, IReadOnlyList<CurrencyUnmatched> UnmatchedByCurrency);

/// <summary>Why a workbench focus could not be resolved. NotFound = the transaction or exception does not exist in the run.</summary>
public enum FocusFailure { None, InvalidTransactionId, NotFound }
