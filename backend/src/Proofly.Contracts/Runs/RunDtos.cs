using Proofly.Contracts.Common;

namespace Proofly.Contracts.Runs;

public sealed record RunDto(
    string Id, string LegalEntityId, DateOnly BusinessDate, int Version, string RulesetVersion,
    string State, string SignOffState, DateTimeOffset StartedAt, DateTimeOffset LastEvidenceArrivalAt,
    string? SupersededByRunId, string? SupersedesRunId, UserRefDto? Preparer, UserRefDto? RequiredApprover,
    UserRefDto? SignedOffBy, DateTimeOffset? SignedOffAt);

public sealed record RunPageDto(IReadOnlyList<RunDto> Items, PageInfoDto Page);
