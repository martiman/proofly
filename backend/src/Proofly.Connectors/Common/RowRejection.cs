namespace Proofly.Connectors.Common;

/// <summary>A source row rejected by an adapter. Adapters reject with a reason and never repair data.</summary>
public sealed record RowRejection(int RowNumber, string Field, string Reason);

public sealed record ParseResult<T>(IReadOnlyList<T> Accepted, IReadOnlyList<RowRejection> Rejected);
