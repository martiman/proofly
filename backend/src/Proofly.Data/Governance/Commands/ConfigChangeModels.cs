using System.Text.Json.Nodes;

namespace Proofly.Data.Governance.Commands;

public enum GovernanceFailure { NotFound, InvalidState, Duplicate, SeparationOfDuties, Forbidden, Validation }

/// <summary>A rejected governance command. The API maps it to the contract problem type.</summary>
public sealed class GovernanceCommandException(GovernanceFailure failure, string detail, string? pointer = null) : Exception(detail)
{
    public GovernanceFailure Failure { get; } = failure;
    public string? Pointer { get; } = pointer;
}

public static class ConfigTargetKinds
{
    public const string Rule = "RULE", Source = "SOURCE", UserAccess = "USER_ACCESS", ApprovalPolicy = "APPROVAL_POLICY";
    public static readonly IReadOnlyList<string> All = [Rule, Source, UserAccess, ApprovalPolicy];
}

public static class ConfigChangeStatuses
{
    public const string Draft = "DRAFT", Pending = "PENDING_APPROVAL", Approved = "APPROVED", Active = "ACTIVE", Rejected = "REJECTED", Retired = "RETIRED";
}

public sealed record ApprovalInfo(string Id, int ProposalVersion, string? ApproverId, string? ApproverName, string RequiredRole, string Status,
    string? Decision, string? Rationale, string PolicyVersion, DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt);

/// <param name="BeforeJson">Mutable-key object of the target before the change (null when unknown).</param>
/// <param name="AfterJson">Normalised proposed object (read-only echo keys removed).</param>
/// <param name="RulesetVersion">Set when an approved CONFIG change was activated: the first ruleset (and run.ruleset_version) that includes it.</param>
public sealed record ConfigChangeRow(string Id, string ChangeKind, string TargetKind, string TargetId, string Status, string Reason,
    string? BeforeJson, string AfterJson, DateOnly EffectiveFrom, string ProposerId, string ProposerName, DateTimeOffset ProposedAt,
    int Version, int? BaseVersion, ApprovalInfo? Approval, string? RulesetVersion)
{
    public bool IsAccess => ChangeKind == "ACCESS";
    public JsonObject After => (JsonObject)JsonNode.Parse(AfterJson)!;
}

public sealed record ConfigChangeCursor(DateTimeOffset At, string Id);
public sealed record ConfigChangeQuery(string? Status, string? TargetKind, int Limit, ConfigChangeCursor? After);
public sealed record ConfigChangePageRows(IReadOnlyList<ConfigChangeRow> Items, bool HasMore);

public sealed record ProposeConfigChangeCommand(string TargetKind, string TargetId, JsonObject After, DateOnly EffectiveFrom, string Reason,
    string ActorId, string? RequestId);

/// <param name="Authorize">Runs after the change is loaded and locked (404 already applied) and before any state check: scope, permission for the change kind, If-Match.</param>
public sealed record DecideConfigChangeCommand(string ChangeId, bool Approve, string Reason, int? ProposalVersion, string ActorId, string? RequestId,
    Action<ConfigChangeRow> Authorize);

public sealed record DecisionResult(ConfigChangeRow Change, string? RulesetVersion);

/// <summary>Ruleset in force at a point in time. <c>Snapshot</c> is the JSON array of enabled config_rules rows at activation.</summary>
public sealed record RulesetVersion(string Version, DateTimeOffset ActivatedAt, DateOnly EffectiveFrom, string? ChangeId, string SnapshotJson);
