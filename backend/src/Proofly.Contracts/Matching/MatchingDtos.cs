using Proofly.Contracts.Common;

namespace Proofly.Contracts.Matching;

/// <summary>Contract schema MatchCandidate. kind, side, score and selected are optional members; timestamp/reference are null when the canonical record is gone.</summary>
public sealed record MatchCandidateDto(
    string TransactionId, string Label, MoneyDto Amount, DateTimeOffset? Timestamp, string? Reference,
    string Kind, string Side, string? Score, bool Selected);

public sealed record MatchApprovalDto(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefDto Proposer, UserRefDto? Approver,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt, bool CanDecide = false);

public sealed record MatchGroupDto(
    string Id, string Status, string Cardinality, IReadOnlyList<MatchCandidateDto> Left, IReadOnlyList<MatchCandidateDto> Right, MoneyDto? Difference,
    string? RuleVersion, string? Tolerance, string? Rationale, IReadOnlyList<string> ExceptionIds, bool Manual, MatchApprovalDto? Approval,
    string? Etag = null, IReadOnlyList<string>? AllowedActions = null);

public sealed record UnmatchedCurrencyDto(string Currency, int Count, MoneyDto Value);

public sealed record MatchingStatsDto(string? AutoMatchRate, int OpenGroups, int UnmatchedCount, IReadOnlyList<UnmatchedCurrencyDto> UnmatchedByCurrency);

public sealed record MatchingWorkbenchDto(
    string? FocusTransactionId, IReadOnlyList<MatchGroupDto> Groups, IReadOnlyList<MatchCandidateDto> Unmatched, MatchingStatsDto Stats);
