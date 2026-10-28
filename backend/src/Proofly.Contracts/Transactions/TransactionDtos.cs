using System.Text.Json.Serialization;
using Proofly.Contracts.Common;

namespace Proofly.Contracts.Transactions;

public sealed record TransactionDto(
    string Id, string RunId, string Kind, string Source, string SourceRowId, string? RecordId, string? DeliveryId,
    string Reference, string? AccountId, DateTimeOffset Timestamp, MoneyDto Amount, string? Direction, string SourceStatus,
    IReadOnlyList<string> Lifecycle, string MatchStatus, string? MatchGroupId, IReadOnlyList<string> ExceptionIds, bool Accepted);

public sealed record TransactionAggregateDto(string Currency, int Count, MoneyDto DebitTotal, MoneyDto CreditTotal);
public sealed record TransactionPageDto(IReadOnlyList<TransactionDto> Items, PageInfoDto Page, IReadOnlyList<TransactionAggregateDto> Aggregates);

public sealed record EvidenceNodeDto(string Id, string Kind, string Label, string Source, string SourceRowId, string? RecordId, MoneyDto? Amount);
public sealed record EvidenceEdgeDto(string From, string To, string Relation);
public sealed record LineageDeliveryDto(string DeliveryId, string Fingerprint, int? RowNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SchemaVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MappingVersion);
public sealed record LineageDto(string SubjectKind, string SubjectId, IReadOnlyList<EvidenceNodeDto> Nodes, IReadOnlyList<EvidenceEdgeDto> Edges,
    IReadOnlyList<LineageDeliveryDto> Deliveries);

public sealed record AccountDto(
    string Id, string RunId, string Currency, string LegalEntityId, MoneyDto? OpeningBalance, MoneyDto? RecordedClosingBalance,
    MoneyDto? IndependentClosingBalance, MoneyDto? Difference, string State, IReadOnlyList<string> ExceptionIds);

public sealed record AccountAggregateDto(string Currency, int Accounts, MoneyDto TotalDifference);
public sealed record AccountPageDto(IReadOnlyList<AccountDto> Items, PageInfoDto Page, IReadOnlyList<AccountAggregateDto> Aggregates);

public sealed record LedgerEntryDto(
    string Id, string AccountId, DateTimeOffset Timestamp, DateOnly ValueDate, string Direction, MoneyDto Amount, string Reference,
    string EntryType, string SourceStatus, string? RecordId, IReadOnlyList<string> ExceptionIds);

public sealed record RollForwardLineDto(string Label, MoneyDto Amount);

public sealed record AccountExceptionSummaryDto(
    string Id, string RunId, string Title, string Control, string ReasonCode, string Severity, string Status, MoneyDto? Value,
    string LegalEntityId, DateOnly BusinessDate, UserRefDto? Owner, DateTimeOffset CreatedAt, DateTimeOffset DueAt, bool Blocking,
    string? SourceFile, string? SourceRowId, string Etag);

public sealed record AccountLedgerViewDto(
    AccountDto Account, IReadOnlyList<LedgerEntryDto> Entries, PageInfoDto EntriesPage, IReadOnlyList<RollForwardLineDto> RollForward,
    IReadOnlyList<TransactionDto> RelatedTransactions, IReadOnlyList<AccountExceptionSummaryDto> Exceptions,
    IReadOnlyList<string> AvailableCurrencies);
