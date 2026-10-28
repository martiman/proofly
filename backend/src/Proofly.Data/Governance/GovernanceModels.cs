using System.Text.Json.Nodes;

namespace Proofly.Data.Governance;

public sealed record LegalEntityRow(string Id, string Name, IReadOnlyList<string> Currencies);
public sealed record RuleConfigRow(string RuleId, string Control, string Version, string Description, string Tolerance, string Severity, string EffectiveFrom, string? PendingChangeId);
public sealed record SourceConfigRow(string SourceName, string SchemaVersion, string ExpectedByLocalTime, bool Mandatory, string? PendingChangeId);
public sealed record UserAccessRow(string Id, string Name, string Email, IReadOnlyList<string> Roles, IReadOnlyList<string> LegalEntityIds, string? PendingChangeId);
public sealed record ApprovalPolicyRow(string Name, string Description, string? RequiredRole, string? Version);
public sealed record SettingsSnapshot(LegalEntityRow LegalEntity, IReadOnlyList<RuleConfigRow> Rules, IReadOnlyList<SourceConfigRow> Sources,
    IReadOnlyList<UserAccessRow> Users, string FreshnessPolicy, IReadOnlyList<ApprovalPolicyRow> ApprovalPolicies, int PendingChanges);

public sealed record UserRefRow(string Id, string Name);
public sealed record ApprovalRow(string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefRow Proposer,
    UserRefRow? Approver, string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs,
    string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt, bool CanDecide);
public sealed record ApprovalCursor(DateTimeOffset At, string Id);
public sealed record ApprovalQuery(string CallerId, IReadOnlyCollection<string>? LegalEntityIds, string? ObjectType, bool Mine, int Limit, ApprovalCursor? After);
public sealed record ApprovalPageRows(IReadOnlyList<ApprovalRow> Items, bool HasMore);

public sealed record EvidencePackRow(string? Id, string RunId, DateTimeOffset? GeneratedAt, string Status, string? Sha256, long? SizeBytes, IReadOnlyList<string> Contents, string? FailureReason);
