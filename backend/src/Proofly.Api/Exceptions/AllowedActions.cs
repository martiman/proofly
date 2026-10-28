using Proofly.Domain.Enums;

namespace Proofly.Api.Exceptions;

/// <summary>Inputs for the pure allowed-actions decision.</summary>
public sealed record ActionContext(
    ExceptionStatus Status, bool RunSuperseded, string CallerUserId, string? PendingProposerId,
    Func<Permission, bool> Can);

/// <summary>
/// Which commands the caller may issue on an exception right now. Pure and deterministic: state, permission (already scoped to
/// the exception's legal entity by <see cref="ActionContext.Can"/>) and separation of duties (a proposer never decides their own proposal).
/// </summary>
public static class AllowedActions
{
    public static IReadOnlyList<string> Compute(ActionContext c)
    {
        var result = new List<string>();
        if (c.RunSuperseded) { return result; } // a superseded run is read-only
        var terminal = c.Status is ExceptionStatus.Resolved or ExceptionStatus.AcceptedDifference or ExceptionStatus.FalsePositive;
        if (terminal)
        {
            if (c.Can(Permission.ExceptionReopen)) { result.Add("REOPEN"); }
            return result;
        }
        if (c.Can(Permission.ExceptionAssign)) { result.Add("ASSIGN"); }
        if (c.Can(Permission.ExceptionInvestigate)) { result.Add("ADD_NOTE"); result.Add("REQUEST_EVIDENCE"); }
        if (c.Status == ExceptionStatus.PendingApproval)
        {
            if (c.PendingProposerId is not null && c.PendingProposerId != c.CallerUserId && c.Can(Permission.ExceptionApprove))
            {
                result.Add("APPROVE_RESOLUTION");
                result.Add("REJECT_RESOLUTION");
            }
            // A new proposal replaces (invalidates) the pending one.
            if (c.Can(Permission.ExceptionPropose)) { result.Add("PROPOSE_RESOLUTION"); }
        }
        else if (c.Can(Permission.ExceptionPropose))
        {
            result.Add("PROPOSE_RESOLUTION");
        }
        return result;
    }
}
