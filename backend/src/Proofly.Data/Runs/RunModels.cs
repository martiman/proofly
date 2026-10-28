namespace Proofly.Data.Runs;

/// <summary>Legal entities a caller may read (already permission-filtered by the API). All = every entity.</summary>
public sealed record EntityScope(bool All, IReadOnlyList<string> Ids)
{
    public static EntityScope Everything { get; } = new(true, []);
    public static EntityScope Only(params string[] ids) => new(false, ids);
    public bool Contains(string legalEntityId) => All || Ids.Contains(legalEntityId);
}

public sealed record LegalEntityRow(string Id, string Name, IReadOnlyList<string> Currencies);

public sealed record UserRefRow(string Id, string Name);

public sealed record RunRow(
    string Id, string LegalEntityId, DateOnly BusinessDate, int Version, string RulesetVersion, string State, string SignOffState,
    DateTimeOffset StartedAt, DateTimeOffset? LastEvidenceArrivalAt, long RowVersion, string? SupersededByRunId, string? SupersedesRunId,
    UserRefRow? Preparer, UserRefRow? RequiredApprover, UserRefRow? SignedOffBy, DateTimeOffset? SignedOffAt);

public sealed record RunListFilter(string? LegalEntityId, DateOnly? BusinessDate, bool IncludeSuperseded);

/// <summary>Keyset state for runs: business_date DESC, legal_entity_id ASC, run_version DESC.</summary>
public sealed record RunCursor(DateOnly BusinessDate, string LegalEntityId, int Version, string Id);

public sealed record RunPageRows(IReadOnlyList<RunRow> Items, int? Total);

public sealed record MoneyRow(decimal Amount, string Currency);

public sealed record ControlAreaRow(
    string Area, string Label, string State, int RecordCount, IReadOnlyList<MoneyRow> Value, int OpenExceptions, int PreviousRecordCount, int ChangeFromPrevious);

public sealed record DailySummaryRow(string Text, string? LinkKind, string? LinkId);

public sealed record DeliveryRow(
    string Id, string SourceName, string FileName, DateOnly BusinessDate, string SchemaVersion, int? DeclaredRecordCount, int ActualRecordCount,
    IReadOnlyList<MoneyRow> DeclaredTotals, DateTimeOffset? ExpectedBy, DateTimeOffset? ReceivedAt, string? CorrectionOfDeliveryId,
    string? SupersededByDeliveryId, string State, int AcceptedRecords, int RejectedRecords, IReadOnlyList<string> FindingIds);

public sealed record AttentionRow(
    string Id, string RunId, string Title, string Control, string ReasonCode, string Severity, string Status, MoneyRow? Value, string LegalEntityId,
    DateOnly BusinessDate, UserRefRow? Owner, DateTimeOffset CreatedAt, int AgeDays, DateTimeOffset DueAt, bool Blocking, string? SourceFile,
    string? SourceRowId, long RowVersion);

public sealed record SafeguardingRow(
    string Currency, MoneyRow Obligation, MoneyRow EligibleBalance, MoneyRow ApprovedAdjustments, MoneyRow Result, string State,
    IReadOnlyList<string> Qualifications, MoneyRow? PreviousResult, IReadOnlyList<string> ExceptionIds);

public sealed record ControlRoomRows(
    RunRow Run, IReadOnlyList<DailySummaryRow> Summary, IReadOnlyList<DeliveryRow> EvidenceReadiness, IReadOnlyList<ControlAreaRow> ControlAreas,
    IReadOnlyList<AttentionRow> AttentionQueue, IReadOnlyList<SafeguardingRow> Safeguarding);
