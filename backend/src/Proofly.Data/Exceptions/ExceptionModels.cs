namespace Proofly.Data.Exceptions;

/// <summary>Filters shared by the list and the summary. Null means "not filtered". Values are already validated by the caller.</summary>
public sealed record ExceptionFilter
{
    public IReadOnlyList<string>? Severity { get; init; }
    public IReadOnlyList<string>? Control { get; init; }
    public IReadOnlyList<string>? Status { get; init; }
    public string? OwnerId { get; init; }
    public bool? Unassigned { get; init; }
    public bool? Blocking { get; init; }
    public string? LegalEntityId { get; init; }
    public string? Currency { get; init; }
    public decimal? ValueMin { get; init; }
    public decimal? ValueMax { get; init; }
    public DateOnly? BusinessDateFrom { get; init; }
    public DateOnly? BusinessDateTo { get; init; }
    public int? AgeDaysMin { get; init; }
    public int? AgeDaysMax { get; init; }
    public DateTimeOffset? DueBefore { get; init; }
    public string? Search { get; init; }
}

/// <param name="EntityScope">Legal entities the caller may see; null means all.</param>
/// <param name="After">Sort-key values (as text) of the last row of the previous page; null for the first page.</param>
public sealed record ExceptionListQuery(
    string RunId, IReadOnlyCollection<string>? EntityScope, ExceptionFilter Filter, ExceptionSortSpec Sort,
    int Limit, IReadOnlyList<string>? After, bool IncludeTotal, DateTimeOffset Now);

public sealed record ExceptionRow(
    string Id, string RunId, string Title, string Control, string ReasonCode, string Severity, string Status,
    decimal? ValueAmount, string? ValueCurrency, string LegalEntityId, DateOnly BusinessDate,
    string? OwnerId, string? OwnerName, DateTimeOffset CreatedAt, int AgeDays, DateTimeOffset DueAt, bool Blocking,
    string? SourceFile, string? SourceRowId, int Version)
{
    /// <summary>Sort-key values of this row as text, in the order of the requested sort (cursor state).</summary>
    public IReadOnlyList<string> SortKeys { get; init; } = [];
}

/// <summary>Up to Limit + 1 rows so the caller can tell whether another page exists.</summary>
public sealed record ExceptionListResult(IReadOnlyList<ExceptionRow> Rows, int? Total);

public sealed record CurrencyAggregate(string Currency, int Count, decimal TotalValue, decimal AbsoluteValue);

public sealed record ExceptionsSummaryData(
    int Total, int Blocking, int Unassigned, int Overdue, int WithoutValue,
    IReadOnlyDictionary<string, int> BySeverity, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> ByControl,
    IReadOnlyList<CurrencyAggregate> ByCurrency, DateTimeOffset ComputedAt);

public sealed record EvidenceRefRow(long Id, string Kind, string Ref);
public sealed record TimelineRow(long Id, string Kind, string ActorType, string? ActorId, string? ActorName, string Summary, DateTimeOffset At);

public sealed record ApprovalRow(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion,
    string ProposerId, string ProposerName, string? ApproverId, string? ApproverName,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed record ExceptionDetailData(
    ExceptionRow Row, string RunState, string RulesetVersion, string? DeliverySource,
    IReadOnlyList<EvidenceRefRow> Evidence, IReadOnlyList<TimelineRow> Timeline, IReadOnlyList<ApprovalRow> Approvals,
    string? ConfiguredApprovalPolicyVersion);
