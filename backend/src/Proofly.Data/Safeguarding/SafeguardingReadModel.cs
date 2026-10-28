namespace Proofly.Data.Safeguarding;

/// <summary>Legal-entity scope of a caller. All=true means every entity ('*').</summary>
public sealed record EntityScope(bool All, IReadOnlyCollection<string> Ids)
{
    public static EntityScope Only(params string[] ids) => new(false, ids);
    public static EntityScope Everything { get; } = new(true, []);
}

public sealed record SafeguardingRun(string RunId, string LegalEntityId, DateOnly BusinessDate, string SignOffState);

/// <summary>
/// One persisted currency position. Mirrors Proofly.Controls CurrencyPosition: Obligation, Eligible (EligibleTotal),
/// ApprovedAdjustments, Result (Position = eligible + approved adjustments - obligation), State. Never netted across currencies.
/// </summary>
public sealed record PositionRow(
    string Currency, decimal Obligation, decimal Eligible, decimal ApprovedAdjustments, decimal? Result, string State,
    IReadOnlyList<string> Qualifications, IReadOnlyList<string> ExceptionIds, bool Stale = false);

public sealed record ObligationLineRow(string AccountId, string Currency, decimal Amount, string? MovementType);

public sealed record BalanceLineRow(
    string SourceRowId, string? RecordId, string ExternalAccountId, DateTimeOffset BalanceTimestamp, decimal Balance, string Currency,
    string? EvidenceReference, bool Included, string Reason, IReadOnlyList<string> FindingIds);

public sealed record PreviousPosition(string Currency, decimal Obligation, decimal Eligible, decimal ApprovedAdjustments, decimal? Result);

public sealed record UserRefRow(string Id, string Name);

public sealed record ApprovalRow(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefRow Proposer, UserRefRow? Approver,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed record HistoryRow(string Id, DateTimeOffset At, string ActorType, string? ActorId, string? ActorName, string EventType, string Text);

public sealed record AdjustmentRow(
    string Id, string RunId, string Currency, decimal Amount, string Status, UserRefRow Owner, string Reason,
    IReadOnlyList<string> EvidenceRefs, DateOnly? ExpiresOn, int Version, ApprovalRow? Approval, IReadOnlyList<HistoryRow> History,
    DateTimeOffset CreatedAt);

/// <summary>Keyset state for adjustments: oldest first, (created_at, id) ascending.</summary>
public sealed record AdjustmentCursor(DateTimeOffset At, string Id);

public sealed record AdjustmentPageRows(IReadOnlyList<AdjustmentRow> Items, bool HasMore, int? Total);

public sealed record SafeguardingView(
    SafeguardingRun Run, string EvidenceReadiness, IReadOnlyList<PositionRow> Positions, IReadOnlyList<ObligationLineRow> Obligations,
    IReadOnlyList<BalanceLineRow> Balances, IReadOnlyList<AdjustmentRow> Adjustments,
    IReadOnlyList<PreviousPosition> Previous);
