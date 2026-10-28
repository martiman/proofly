namespace Proofly.Contracts.Exceptions;

public sealed record ResolutionProposalRequest(string? Kind, string? Reason, IReadOnlyList<string>? EvidenceRefs);
public sealed record ResolutionDecisionRequest(string? Decision, string? Reason, int? ProposalVersion);
public sealed record ReopenExceptionRequest(string? Reason);
