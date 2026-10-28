using System.Text.Json.Serialization;

namespace Proofly.Contracts.Common;

public sealed record MoneyDto(string Amount, string Currency);

/// <summary>Contract schema PageInfo. nextCursor is always present (null on the last page); total is omitted, never null, unless includeTotal=true.</summary>
public sealed record PageInfoDto(string? NextCursor, bool HasMore, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total = null);

/// <summary>The { items, page } envelope shared by every list that has no extra members.</summary>
public sealed record PageDto<T>(IReadOnlyList<T> Items, PageInfoDto Page);

public sealed record UserRefDto(string Id, string Name);

/// <summary>Contract schema Actor: id is null for SYSTEM actors, name when none is recorded.</summary>
public sealed record ActorDto(string Type, string? Id, string? Name);

public sealed record ProblemDto(string Type, string Title, int Status, string? Detail);
