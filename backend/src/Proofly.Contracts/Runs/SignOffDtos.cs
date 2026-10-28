using Proofly.Contracts.Common;
using Proofly.Contracts.Exceptions;

namespace Proofly.Contracts.Runs;

/// <summary>Body of submitRunForSignOff.</summary>
public sealed record SubmitRunRequestDto(string? RequiredApproverId, string? Note);

/// <summary>Body of signOffRun. Attestation must be true; AttestationTextHash (optional) is the hash of the text the approver saw.</summary>
public sealed record SignOffRunRequestDto(bool? Attestation, string? AttestationTextHash, string? Reason);

/// <summary>Contract schema ChecklistItem.</summary>
public sealed record ChecklistItemDto(string Id, string Control, string Label, string State, bool Mandatory, string Note, IReadOnlyList<string> ExceptionIds, int ExceptionCount);

/// <summary>Open matters of one control area (contract schema OpenMatterCategory).</summary>
public sealed record OpenMatterCategoryDto(string Control, int Total, int Blocking);

/// <summary>Contract schema RunSignOff. AllowedActions (SUBMIT, RETURN, APPROVE) is server-computed for the caller; CanSignOff = APPROVE is allowed.</summary>
public sealed record RunSignOffDto(
    RunDto Run, IReadOnlyList<ChecklistItemDto> Checklist, IReadOnlyList<ExceptionSummaryDto> OpenMatters, int OpenMattersTotal,
    IReadOnlyList<OpenMatterCategoryDto> OpenMattersByCategory, IReadOnlyList<string> Qualifications,
    IReadOnlyList<string> ChangesSincePreparation, string AttestationText, string AttestationTextHash, bool CanSignOff, IReadOnlyList<string> Blockers,
    string? EvidencePackId, ApprovalDto? Approval, IReadOnlyList<TimelineEventDto> History, IReadOnlyList<string> AllowedActions);
