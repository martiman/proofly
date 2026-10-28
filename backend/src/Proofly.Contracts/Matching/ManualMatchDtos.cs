namespace Proofly.Contracts.Matching;

/// <summary>Body of proposeManualMatch.</summary>
public sealed record ProposeManualMatchRequest(IReadOnlyList<string>? LeftTransactionIds, IReadOnlyList<string>? RightTransactionIds, string? Reason, IReadOnlyList<string>? EvidenceRefs);

/// <summary>Body of decideManualMatch (schema DecisionInput).</summary>
public sealed record DecideManualMatchRequest(string? Decision, string? Reason, int? ProposalVersion);
