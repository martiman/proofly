using System.Text.Json.Serialization;
using Proofly.Contracts.Common;

namespace Proofly.Contracts.Governance;

public sealed record AuditEventDto(string Id, DateTimeOffset At, ActorDto Actor, string Type, string? RunId, string SubjectKind, string SubjectId,
    string Summary, string? Reason, string? RuleVersion, string? RequestId, string Hash, string? PreviousHash);

public sealed record LegalEntityDto(string Id, string Name, IReadOnlyList<string> Currencies);
public sealed record RuleConfigDto(string RuleId, string Control, string Version, string Description, string Tolerance, string Severity, string EffectiveFrom, string? PendingChangeId);
public sealed record SourceConfigDto(string SourceName, string SchemaVersion, string ExpectedByLocalTime, bool Mandatory, string? PendingChangeId);
public sealed record UserAccessDto(string Id, string Name, string Email, IReadOnlyList<string> Roles, IReadOnlyList<string> LegalEntityIds, string? PendingChangeId);
public sealed record ApprovalPolicyDto(string Name, string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequiredRole,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version);
public sealed record SettingsDto(LegalEntityDto LegalEntity, IReadOnlyList<RuleConfigDto> Rules, IReadOnlyList<SourceConfigDto> Sources,
    IReadOnlyList<UserAccessDto> Users, string FreshnessPolicy, IReadOnlyList<ApprovalPolicyDto> ApprovalPolicies, int PendingChanges);

public sealed record ApprovalDto(string Id, string ObjectType, string ObjectId, string? RunId, int ProposalVersion, UserRefDto Proposer, UserRefDto? Approver,
    string RequiredRole, string Status, string? Decision, string? Rationale, IReadOnlyList<string> EvidenceRefs, string PolicyVersion,
    DateTimeOffset ProposedAt, DateTimeOffset? DecidedAt, bool CanDecide, string? Kind = null);

public sealed record EvidencePackDto(string? Id, string RunId, DateTimeOffset? GeneratedAt, string Status, IReadOnlyList<string> Contents, string? Sha256, long? SizeBytes, string? FailureReason,
    EvidenceManifestDto? Manifest = null, AuditChainDto? AuditChain = null);

public sealed record EvidenceManifestFileDto(string Path, string Sha256, long SizeBytes);
public sealed record EvidenceManifestDto(string SchemaVersion, string ResultHash, IReadOnlyList<EvidenceManifestFileDto> Files, UserRefDto? GeneratedBy, DateTimeOffset GeneratedAt);
public sealed record AuditChainBreakDto(string ChainId, long ChainSeq, long EventId, string Problem);
public sealed record AuditChainDto(bool Valid, long EventsChecked, AuditChainBreakDto? FirstBreak);
