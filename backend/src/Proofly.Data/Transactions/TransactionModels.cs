namespace Proofly.Data.Transactions;

/// <summary>The run and its legal entity; used to enforce scope before any data is read.</summary>
public sealed record RunScope(string RunId, string LegalEntityId, DateOnly BusinessDate);

public sealed record TransactionFilter(
    string? Kind = null, string? MatchStatus = null, string? AccountId = null, string? Currency = null, bool? HasException = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Search = null);

/// <summary>Keyset state for transactions: stable sort is (timestamp, id) ascending.</summary>
public sealed record TransactionCursor(DateTimeOffset Ts, string Id);

public sealed record TransactionRow(
    string Id, string RunId, string Kind, string Source, string SourceRowId, string? RecordId, string DeliveryId, string Reference,
    string? AccountId, DateTimeOffset Timestamp, decimal Amount, string Currency, string? Direction, string SourceStatus,
    IReadOnlyList<string> Lifecycle, string MatchStatus, string? MatchGroupId, IReadOnlyList<string> ExceptionIds, bool Accepted);

public sealed record CurrencyAggregate(string Currency, int Count, decimal DebitTotal, decimal CreditTotal);

public sealed record TransactionPageResult(IReadOnlyList<TransactionRow> Rows, bool HasMore, int? Total, IReadOnlyList<CurrencyAggregate> Aggregates);

public sealed record LineageNode(string Id, string Kind, string Label, string Source, string SourceRowId, string? RecordId, decimal? Amount, string? Currency);
public sealed record LineageEdge(string From, string To, string Relation);
public sealed record LineageDelivery(string DeliveryId, string Fingerprint, string SchemaVersion);
public sealed record TransactionLineage(string TransactionId, IReadOnlyList<LineageNode> Nodes, IReadOnlyList<LineageEdge> Edges, IReadOnlyList<LineageDelivery> Deliveries);

public sealed record AccountFilter(string? Currency = null, string? State = null, string? Search = null);
public sealed record AccountCursor(string AccountId, string Currency);

public sealed record AccountRow(
    string AccountId, string Currency, string LegalEntityId, decimal Opening, decimal Recorded, decimal Independent,
    decimal Credits, decimal Debits, string State, IReadOnlyList<string> ExceptionIds)
{
    public decimal Difference => Recorded - Independent;
}

public sealed record AccountAggregate(string Currency, int Accounts, decimal TotalDifference);
public sealed record AccountPageResult(IReadOnlyList<AccountRow> Rows, bool HasMore, IReadOnlyList<AccountAggregate> Aggregates);

public sealed record LedgerEntryRow(
    string Id, string AccountId, DateTimeOffset Timestamp, DateOnly ValueDate, string Direction, decimal Amount, string Currency,
    string Reference, string EntryType, string SourceStatus, string? RecordId, IReadOnlyList<string> ExceptionIds, long InternalId);

public sealed record LedgerCursor(DateTimeOffset Ts, long Id);
public sealed record LedgerEntryPage(IReadOnlyList<LedgerEntryRow> Rows, bool HasMore);

public sealed record AccountExceptionRow(
    string Id, string RunId, string Title, string Control, string ReasonCode, string Severity, string Status, decimal? ValueAmount,
    string? ValueCurrency, string LegalEntityId, DateOnly BusinessDate, string? OwnerId, string? OwnerName, DateTimeOffset CreatedAt,
    DateTimeOffset DueAt, bool Blocking, string SourceFile, string SourceRowId, int Version);
