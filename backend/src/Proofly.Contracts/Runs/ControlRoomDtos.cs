using System.Text.Json.Serialization;
using Proofly.Contracts.Common;

namespace Proofly.Contracts.Runs;

public sealed record LegalEntityDto(string Id, string Name, IReadOnlyList<string> Currencies);

public sealed record ControlAreaSummaryDto(
    string Area, string Label, string State, int RecordCount, IReadOnlyList<MoneyDto> Value, int OpenExceptions, int PreviousRecordCount, int ChangeFromPrevious);

public sealed record DailySummaryLinkDto(string Kind, string Id);
public sealed record DailySummaryItemDto(string Text, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DailySummaryLinkDto? Link);

/// <summary>SourceDelivery as embedded in the control room (evidence readiness).</summary>
public sealed record ControlRoomDeliveryDto(
    string DeliveryId, string SourceName, string FileName, DateOnly BusinessDate, string SchemaVersion, int? DeclaredRecordCount, int ActualRecordCount,
    IReadOnlyList<MoneyDto> DeclaredTotals, DateTimeOffset? ExpectedBy, DateTimeOffset? ReceivedAt, string? CorrectionOfDeliveryId,
    string? SupersededByDeliveryId, string State, int AcceptedRecords, int RejectedRecords, IReadOnlyList<string> Qualifications, IReadOnlyList<string> FindingIds);

/// <summary>ExceptionSummary as embedded in the control room (attention queue).</summary>
public sealed record ControlRoomExceptionDto(
    string Id, string RunId, string Title, string Control, string ReasonCode, string Severity, string Status, MoneyDto? Value, string LegalEntityId,
    DateOnly BusinessDate, UserRefDto? Owner, DateTimeOffset CreatedAt, int AgeDays, DateTimeOffset DueAt, bool Blocking, string? SourceFile, string? SourceRowId, string Etag);

public sealed record SafeguardingCurrencyPositionDto(
    string Currency, MoneyDto Obligation, MoneyDto EligibleBalance, MoneyDto ApprovedAdjustments, MoneyDto? Result, string State,
    IReadOnlyList<string> Qualifications, MoneyDto? PreviousResult, IReadOnlyList<string> ExceptionIds, bool Stale);

public sealed record ControlRoomDto(
    RunDto Run, IReadOnlyList<DailySummaryItemDto> Summary, IReadOnlyList<ControlRoomDeliveryDto> EvidenceReadiness,
    IReadOnlyList<ControlAreaSummaryDto> ControlAreas, IReadOnlyList<ControlRoomExceptionDto> AttentionQueue,
    IReadOnlyList<SafeguardingCurrencyPositionDto> Safeguarding);
