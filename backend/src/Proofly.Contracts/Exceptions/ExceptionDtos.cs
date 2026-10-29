using Proofly.Contracts.Common;

namespace Proofly.Contracts.Exceptions;

/// <summary>Contract schema ExceptionSummary. Enum-typed fields carry the contract wire names.</summary>
public record ExceptionSummaryDto(
    string Id, string RunId, string Title, string Description, string Control, string ReasonCode, string Severity, string Status, MoneyDto? Value,
    string LegalEntityId, string BusinessDate, UserRefDto? Owner, DateTimeOffset CreatedAt, int AgeDays, DateTimeOffset DueAt,
    bool Blocking, string? SourceFile, string? SourceRowId, string Etag);

public sealed record ReasonCountDto(string ReasonCode, int Count);
public sealed record SeverityCurrencyDto(string Severity, string Currency, int Count, MoneyDto AbsoluteValue);

public sealed record CurrencyAggregateDto(string Currency, int Count, MoneyDto TotalValue, MoneyDto AbsoluteValue);

public sealed record ExceptionsSummaryDto(
    int Total, int Blocking, int Unassigned, int Overdue,
    IReadOnlyDictionary<string, int> BySeverity, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> ByControl,
    IReadOnlyList<CurrencyAggregateDto> ByCurrency, IReadOnlyList<ReasonCountDto> ByReason, IReadOnlyList<SeverityCurrencyDto> BySeverityCurrency,
    int WithoutValue, DateTimeOffset ComputedAt);

public sealed record EvidenceNodeDto(string Id, string Kind, string Label, string Source, string? SourceRowId, string? RecordId, MoneyDto? Amount);
public sealed record EvidenceEdgeDto(string From, string To, string Relation);
public sealed record RuleTraceInputDto(string Name, string Value);
public sealed record RuleTraceDto(string Control, string RuleId, string RuleVersion, IReadOnlyList<RuleTraceInputDto> Inputs, string? Tolerance, string Calculation, string Result);
public sealed record TimelineEventDto(string Id, DateTimeOffset At, ActorDto Actor, string Type, string Text);

public sealed record ApprovalDto(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefDto Proposer, UserRefDto? Approver,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt, bool CanDecide, string? Kind = null);

/// <summary>ExceptionDetail = ExceptionSummary + detail members (flattened allOf).</summary>
public sealed record ExceptionDetailDto(
    string Id, string RunId, string Title, string Control, string ReasonCode, string Severity, string Status, MoneyDto? Value,
    string LegalEntityId, string BusinessDate, UserRefDto? Owner, DateTimeOffset CreatedAt, int AgeDays, DateTimeOffset DueAt,
    bool Blocking, string? SourceFile, string? SourceRowId, string Etag,
    string Description, string ExpectedEffect, string Summary,
    IReadOnlyList<EvidenceNodeDto> EvidenceNodes, IReadOnlyList<EvidenceEdgeDto> EvidenceEdges, RuleTraceDto Trace,
    IReadOnlyList<TimelineEventDto> Timeline, IReadOnlyList<string> States,
    IReadOnlyList<string> RelatedTransactionIds, IReadOnlyList<string> RelatedAccountIds, string ApprovalPolicy,
    ApprovalDto? PendingApproval, IReadOnlyList<ApprovalDto> Approvals, IReadOnlyList<string> AllowedActions);

public sealed record AssignableUserDto(string Id, string Name, IReadOnlyList<string> Roles);
