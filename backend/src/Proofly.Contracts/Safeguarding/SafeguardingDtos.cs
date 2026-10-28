using Proofly.Contracts.Common;

namespace Proofly.Contracts.Safeguarding;

public sealed record SafeguardingCurrencyPositionDto(
    string Currency, MoneyDto Obligation, MoneyDto EligibleBalance, MoneyDto ApprovedAdjustments, MoneyDto Result,
    string State, IReadOnlyList<string> Qualifications, MoneyDto? PreviousResult, IReadOnlyList<string> ExceptionIds);

public sealed record ObligationLineDto(string AccountId, string Currency, MoneyDto Amount,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? MovementType);

public sealed record EligibleBalanceLineDto(
    string SourceRowId, string? RecordId, string ExternalAccountId, DateTimeOffset BalanceTimestamp, MoneyDto Balance,
    string? EvidenceReference, bool Included, string Reason, IReadOnlyList<string> FindingIds);

public sealed record ChangeDriverDto(string Label, string Currency, MoneyDto Change);

public sealed record TimelineEventDto(string Id, DateTimeOffset At, ActorDto Actor, string Type, string Text);

public sealed record ApprovalDto(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefDto Proposer, UserRefDto? Approver,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt, bool CanDecide);

public sealed record SafeguardingAdjustmentDto(
    string Id, string RunId, string Currency, MoneyDto Amount, string Status, UserRefDto Owner, string Reason,
    IReadOnlyList<string> EvidenceRefs, DateOnly? ExpiresOn, ApprovalDto? Approval, IReadOnlyList<TimelineEventDto> History);

public sealed record SafeguardingPositionDto(
    string RunId, DateOnly BusinessDate, string LegalEntityId, string EvidenceReadiness, string ApprovalState,
    IReadOnlyList<SafeguardingCurrencyPositionDto> Positions, IReadOnlyList<ObligationLineDto> Obligations,
    IReadOnlyList<EligibleBalanceLineDto> EligibleBalances, IReadOnlyList<SafeguardingAdjustmentDto> Adjustments,
    IReadOnlyList<ChangeDriverDto> ChangeAnalysis);

