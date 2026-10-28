using Proofly.Data.Exceptions;

namespace Proofly.Data.Runs.Commands;

public enum SignOffStatus { Ok, NotFound, VersionMismatch, InvalidState, RunSuperseded, Blocked, StaleSignOff, SeparationOfDuties, Forbidden, Invalid }

/// <summary>
/// Outcome of a sign-off command. Everything but Ok and StaleSignOff rolled the transaction back (no change, no audit event).
/// StaleSignOff is committed on purpose: the sign-off returned to PREPARATION_INCOMPLETE and that decision is audited.
/// </summary>
public sealed record SignOffResult(SignOffStatus Status, string? Detail = null, string? Pointer = null)
{
    public static readonly SignOffResult Ok = new(SignOffStatus.Ok);
}

/// <param name="ExpectedRunVersion">runs.version the caller read (ETag part one).</param>
/// <param name="ExpectedSignOffVersion">run_signoffs.version of the latest sign-off row, 0 when none (ETag part two).</param>
public sealed record SignOffCommandContext(string ActorUserId, string? RequestId, int ExpectedRunVersion, int ExpectedSignOffVersion);

public sealed record SignOffRow(
    long Id, string State, string? PreparedByUserId, string? RequiredApproverUserId, string? ApprovalId, string? EvidencePackId, int Version,
    string? SubmittedResultHash, string? SubmittedOpenSetHash, string? SubmittedExceptionStateHash, string? AttestationHash, string? ReturnedReason);

/// <param name="ExceptionIds">The first <see cref="SignOffRepository.OpenMattersDefaultLimit"/> open exceptions of the control in open-matter order.</param>
/// <param name="OpenCount">Every open exception of the control.</param>
public sealed record ChecklistRow(string Control, string State, int FindingCount, int BlockingFindingCount, IReadOnlyList<string> ExceptionIds, int OpenCount);

/// <summary>Open exceptions of one control area: all and blocking.</summary>
public sealed record OpenMatterCategoryRow(string Control, int Total, int Blocking);

/// <summary>
/// Keyset state of the open-matter order: blocking first (0), severity rank (CRITICAL 0 .. LOW 3), then absolute value descending (stored negated;
/// exceptions without value sort last), then id (ordinal).
/// </summary>
public sealed record OpenMatterCursor(int Blocking, int Severity, decimal NegAbs, string Id);

public sealed record OpenMatterItem(ExceptionRow Row, OpenMatterCursor Key);

public sealed record OpenMatterFilter(bool? Blocking, string? Severity);

/// <summary>Up to limit + 1 rows; Total counts the filtered open set when requested.</summary>
public sealed record OpenMatterPageRows(IReadOnlyList<OpenMatterItem> Items, int? Total, string LegalEntityId);

public sealed record SignOffApprovalRow(
    string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefRow Proposer, UserRefRow? Approver, string RequiredRole,
    string Status, string? Decision, string? Rationale, string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

public sealed record SignOffHistoryRow(long Id, DateTimeOffset At, string Type, string? ActorId, string ActorName, string ActorType, string? Reason);

/// <summary>Everything the sign-off view needs; money-free (exceptions carry their own decimal values).</summary>
public sealed record SignOffPackRows(
    RunRow Run, SignOffRow? SignOff, IReadOnlyList<ChecklistRow> Checklist, IReadOnlyList<ExceptionRow> OpenMatters, int OpenMattersTotal,
    IReadOnlyList<OpenMatterCategoryRow> OpenMattersByCategory, IReadOnlyList<string> Qualifications,
    IReadOnlyList<string> ChangesSincePreparation, IReadOnlyList<string> Blockers, string AttestationText, string AttestationTextHash,
    string? EvidencePackId, SignOffApprovalRow? Approval, IReadOnlyList<SignOffHistoryRow> History, bool StaleSinceSubmission,
    IReadOnlyList<SignOffBlocker>? BlockerDetails = null);

/// <summary>Structured sign-off blocker. Code: RUN_STATE, NO_RESULT_HASH, COMPLETENESS_MISSING, COMPLETENESS_NOT_PASSED, BLOCKING_EXCEPTION.</summary>
public sealed record SignOffBlocker(string Code, string Message, string? ExceptionId, string? Control);
