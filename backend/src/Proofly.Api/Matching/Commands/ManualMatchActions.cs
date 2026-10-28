using Proofly.Data.Matching;
using Proofly.Domain.Enums;

namespace Proofly.Api.Matching.Commands;

/// <summary>
/// Which manual-match commands the caller may issue on a group right now. Pure and deterministic: state, permission (already scoped to the
/// group's legal entity by <paramref name="can"/>) and separation of duties (the proposer never decides their own proposal).
/// </summary>
public static class ManualMatchActions
{
    public const string Propose = "PROPOSE_MANUAL_MATCH";
    public const string Approve = "APPROVE_MANUAL_MATCH";
    public const string Reject = "REJECT_MANUAL_MATCH";

    public static bool CanDecide(string status, MatchApprovalRow? approval, string callerUserId, bool runLocked, Func<Permission, bool> can) =>
        !runLocked && status == "PENDING_APPROVAL" && approval is { Status: "PENDING", ObjectType: "MANUAL_MATCH" }
        && approval.ProposerId != callerUserId && can(Permission.ExceptionApprove);

    public static IReadOnlyList<string> Compute(string status, MatchApprovalRow? approval, string callerUserId, bool runLocked, Func<Permission, bool> can)
    {
        if (runLocked) { return []; }
        if (CanDecide(status, approval, callerUserId, runLocked, can)) { return [Approve, Reject]; }
        if (status is "UNMATCHED" or "VALUE_MISMATCH" && can(Permission.MatchingPropose)) { return [Propose]; }
        return [];
    }

    /// <summary>A superseded or signed-off run is read-only.</summary>
    public static bool RunLocked(string runState) => runState is "SUPERSEDED" or "SIGNED_OFF";
}
