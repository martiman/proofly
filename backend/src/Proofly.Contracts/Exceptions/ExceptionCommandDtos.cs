using System.Text.Json;
using System.Text.Json.Serialization;
using Proofly.Contracts.Common;

namespace Proofly.Contracts.Exceptions;

public sealed record AssignExceptionRequest(string? OwnerId, string? Note);
public sealed record ExceptionNoteRequest(string Text);
public sealed record RequestEvidenceRequest(string Text, string? RequestedFrom);

public sealed record BulkAssignItemDto(string ExceptionId, string Etag);
public sealed record BulkAssignRequest(string OwnerId, string? Note, IReadOnlyList<BulkAssignItemDto> Items);

/// <summary>Per-item result of bulkAssignExceptions (contract schema BulkResult item). etag only on success, problem only on failure.</summary>
public sealed record BulkItemResultDto(
    string ExceptionId, int Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Etag,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BulkProblemDto? Problem);

/// <summary>Contract schema Problem as embedded in a bulk item (optional members are omitted, never null).</summary>
public sealed record BulkProblemDto(string Type, string Title, int Status, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);

public sealed record BulkResultDto(IReadOnlyList<BulkItemResultDto> Results);

public sealed record SavedViewInputDto(string Name, bool? Shared, JsonElement Filters, string? Sort);

public sealed record SavedViewDto(
    string Id, string Name, bool Shared, JsonElement Filters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sort,
    UserRefDto Owner, DateTimeOffset CreatedAt);

public sealed record SavedViewListDto(IReadOnlyList<SavedViewDto> Items);
