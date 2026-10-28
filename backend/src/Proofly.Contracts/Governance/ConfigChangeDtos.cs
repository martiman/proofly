using System.Text.Json;
using Proofly.Contracts.Common;

namespace Proofly.Contracts.Governance;

public sealed record ConfigDiffDto(string Path, JsonElement? From, JsonElement? To);

/// <summary>Mirrors schema ConfigChange. before/after are mutable-key objects of the target (see docs/integrations/api/README.md).</summary>
public sealed record ConfigChangeDto(string Id, string TargetKind, string TargetId, string Status, bool Material,
    Dictionary<string, JsonElement>? Before, Dictionary<string, JsonElement> After, IReadOnlyList<ConfigDiffDto> Diff,
    UserRefDto Proposer, DateTimeOffset ProposedAt, ApprovalDto? Approval, string EffectiveFrom, string Reason, string? RulesetVersion, string Etag);
