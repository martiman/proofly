using Proofly.Contracts.Common;

namespace Proofly.Contracts.Matching;

public sealed record MatchCandidateDto(string TransactionId, string Label, MoneyDto Amount, DateTimeOffset Timestamp, string Reference);

public sealed record MatchApprovalDto(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefDto Proposer, UserRefDto? Approver,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed record MatchGroupDto(
    string Id, string Status, IReadOnlyList<MatchCandidateDto> Left, IReadOnlyList<MatchCandidateDto> Right, MoneyDto? Difference,
    string RuleVersion, string Tolerance, string Rationale, IReadOnlyList<string> ExceptionIds, bool Manual, MatchApprovalDto? Approval);

public sealed record MatchingWorkbenchDto(string FocusTransactionId, IReadOnlyList<MatchGroupDto> Groups, IReadOnlyList<MatchCandidateDto> Unmatched);
