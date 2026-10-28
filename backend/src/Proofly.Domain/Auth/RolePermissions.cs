using Proofly.Domain.Enums;
using P = Proofly.Domain.Enums.Permission;

namespace Proofly.Domain.Auth;

/// <summary>
/// Single source of the fixed role to permission bundles (ADR 0005). Migration 0007 seeds the same bundles into
/// role_permissions; a test keeps them equal. Anything not granted here is denied.
/// </summary>
public static class RolePermissions
{
    private static readonly P[] Viewer =
    [
        P.AuthSelf, P.RunView, P.SourceView, P.ExceptionView, P.TransactionView, P.MatchingView, P.LedgerView,
        P.SafeguardingView, P.SignoffView, P.EvidenceView, P.EvidenceExport, P.AuditView, P.ApprovalView,
    ];

    private static readonly P[] Analyst =
    [
        P.AuthSelf, P.RunView, P.SourceView, P.SourceRetry, P.ExceptionView, P.ExceptionAssign, P.ExceptionInvestigate,
        P.ExceptionPropose, P.ExceptionReopen, P.TransactionView, P.MatchingView, P.MatchingPropose, P.LedgerView,
        P.SafeguardingView, P.SafeguardingAdjustmentPropose, P.SignoffView, P.SignoffPrepare, P.EvidenceView, P.ApprovalView,
    ];

    private static readonly IReadOnlyDictionary<Role, IReadOnlySet<P>> Bundles = new Dictionary<Role, IReadOnlySet<P>>
    {
        [Role.Viewer] = Viewer.ToHashSet(),
        [Role.Analyst] = Analyst.ToHashSet(),
        [Role.Approver] = Analyst.Concat([P.RunReprocess, P.SourceSupersede, P.ExceptionApprove, P.SafeguardingAdjustmentApprove,
            P.SignoffDecide, P.EvidenceExport, P.AuditView, P.SettingsView]).ToHashSet(),
        [Role.ConfigAdmin] = new HashSet<P> { P.AuthSelf, P.RunView, P.SettingsView, P.SettingsDraft, P.SettingsApprove, P.ApprovalView },
        [Role.SecurityAdmin] = new HashSet<P> { P.AuthSelf, P.RunView, P.AccessManage, P.SettingsView, P.ApprovalView, P.AuditView },
    };

    public static IReadOnlySet<P> For(Role role) => Bundles[role];

    public static IEnumerable<Role> Roles => Bundles.Keys;

    /// <summary>Contract role name (ANALYST, CONFIG_ADMIN, ...); unknown names are not roles.</summary>
    public static bool TryParseRole(string? wire, out Role role)
    {
        foreach (var r in Bundles.Keys)
        {
            if (string.Equals(WireNames.ToUpperSnake(r), wire, StringComparison.Ordinal)) { role = r; return true; }
        }
        role = default;
        return false;
    }
}
