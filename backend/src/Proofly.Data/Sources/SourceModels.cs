namespace Proofly.Data.Sources;

/// <summary>Legal-entity scope of the caller. All = every entity; otherwise only Ids. Applied inside every query.</summary>
public sealed record EntityScope(bool All, IReadOnlyList<string> Ids)
{
    public static EntityScope Everything { get; } = new(true, []);
    public static EntityScope Only(params string[] ids) => new(false, ids);
}

public sealed record MoneyAmount(decimal Amount, string Currency);

public sealed record DeliverySummary(
    string Id, string? RunId, string? LegalEntityId, string SourceName, DateOnly BusinessDate, string SchemaVersion, string FileName,
    string Fingerprint, int? DeclaredRecordCount, DateTimeOffset? ExpectedBy, DateTimeOffset? ReceivedAt, string State,
    int AcceptedRecords, int RejectedRecords, int? TotalRowCount, string? CorrectionOfDeliveryId, string? SupersededByDeliveryId,
    int? SequenceNumber, bool Ingested, int Version, IReadOnlyList<string> FindingIds,
    IReadOnlyList<MoneyAmount> DeclaredTotals, IReadOnlyList<MoneyAmount> ActualTotals, IReadOnlyList<string> Qualifications);

/// <summary>Delivery-level failure (no row number): duplicate, sequence gap, schema, control count/value mismatch, ...</summary>
public sealed record DeliveryFailureRow(string ReasonCode, string Reason);

public sealed record DeliveryDetail(DeliverySummary Delivery, IReadOnlyList<DeliveryFailureRow> Failures);

public sealed record SourceRecordRow(
    string RecordId, string SourceName, string SourceRowId, string DeliveryId, int? RowNumber, bool Accepted, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string>? RejectionReasons, string? ContentFingerprint, IReadOnlyList<string> FindingIds, int Group, long Ordinal);

public sealed record RecordPage(IReadOnlyList<SourceRecordRow> Items, int? Total);

public sealed record RecordQuery(bool? Accepted, string? Search, int Limit, int? AfterGroup, long? AfterOrdinal, bool IncludeTotal);

public sealed record ProcessingStepRow(string Id, DateTimeOffset At, string Stage, string Outcome, string ActorType, string? ActorId, string? ActorName, string? Message);
