using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Proofly.Contracts.Common;

namespace Proofly.Contracts.Sources;

/// <summary>Contract schema SourceDelivery.</summary>
public sealed record SourceDeliveryDto(
    string DeliveryId, string SourceName, string FileName, DateOnly BusinessDate, string SchemaVersion, int DeclaredRecordCount,
    int ActualRecordCount, IReadOnlyList<MoneyDto> DeclaredTotals, DateTimeOffset ExpectedBy, DateTimeOffset? ReceivedAt,
    string? CorrectionOfDeliveryId, string? SupersededByDeliveryId, string State, int AcceptedRecords, int RejectedRecords,
    IReadOnlyList<string> Qualifications, IReadOnlyList<string> FindingIds);

public sealed record SourceDeliveryListDto(IReadOnlyList<SourceDeliveryDto> Items);

public sealed record FingerprintDto(string Algorithm, string Value);

public sealed record CheckResultDto(string Check, string Result, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail, string? RelatedDeliveryId);

/// <summary>Contract schema SourceDeliveryDetail (allOf SourceDelivery + detail), flattened.</summary>
public sealed record SourceDeliveryDetailDto(
    string DeliveryId, string SourceName, string FileName, DateOnly BusinessDate, string SchemaVersion, int DeclaredRecordCount,
    int ActualRecordCount, IReadOnlyList<MoneyDto> DeclaredTotals, DateTimeOffset ExpectedBy, DateTimeOffset? ReceivedAt,
    string? CorrectionOfDeliveryId, string? SupersededByDeliveryId, string State, int AcceptedRecords, int RejectedRecords,
    IReadOnlyList<string> Qualifications, IReadOnlyList<string> FindingIds,
    FingerprintDto Fingerprint, int? SequenceNumber, DateTimeOffset? ExtractionTimestamp, string? SourceTimeZone,
    IReadOnlyList<CheckResultDto> Checks, IReadOnlyList<MoneyDto> ActualTotals, IReadOnlyList<string> AllowedActions);

public sealed record SourceRecordDto(
    string RecordId, string SourceName, string SourceRowId, string DeliveryId, int RowNumber, bool Accepted, JsonObject Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? RejectionReasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentFingerprint,
    IReadOnlyList<string> FindingIds);

public sealed record ProcessingStepDto(
    string Id, DateTimeOffset At, string Stage, string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActorDto? Actor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

public sealed record ProcessingHistoryDto(IReadOnlyList<ProcessingStepDto> Items);
