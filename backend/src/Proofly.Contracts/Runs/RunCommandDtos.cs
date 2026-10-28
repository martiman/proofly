using Proofly.Contracts.Common;

namespace Proofly.Contracts.Runs;

/// <summary>Body of requestRunReprocess (contract requestBody ReasonOnly).</summary>
public sealed record RunReprocessRequestDto(string? Reason);

/// <summary>Contract schema ChangeDriver.</summary>
public sealed record ChangeDriverDto(string Label, string Currency, MoneyDto Change);

public sealed record RunComparisonAreaDto(string Area, string State, string AgainstState, int RecordCountDelta);

/// <summary>Contract schema RunComparison.</summary>
public sealed record RunComparisonDto(
    RunDto Run, RunDto Against, IReadOnlyList<RunComparisonAreaDto> ControlAreas, IReadOnlyList<string> NewExceptionIds,
    IReadOnlyList<string> ResolvedExceptionIds, IReadOnlyList<ChangeDriverDto> SafeguardingDelta, IReadOnlyList<string> ChangedDeliveries);
