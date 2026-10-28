namespace Proofly.Contracts.Common;

public sealed record MoneyDto(string Amount, string Currency);
public sealed record PageInfoDto(string? NextCursor, bool HasMore, int? Total);
public sealed record UserRefDto(string Id, string Name);
public sealed record ActorDto(string Type, string Id, string Name);
public sealed record ProblemDto(string Type, string Title, int Status, string? Detail);
