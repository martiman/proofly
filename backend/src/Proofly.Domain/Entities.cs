using Proofly.Domain.Enums;
using Proofly.Domain.Ids;

namespace Proofly.Domain;

public sealed record Run(
    RunId Id, LegalEntityId LegalEntityId, DateOnly BusinessDate, int Version, string RulesetVersion,
    RunState State, SignOffState SignOffState, DateTimeOffset StartedAt, DateTimeOffset LastEvidenceArrivalAt);

public sealed record Actor(ActorType Type, string Id, string Name);

/// <summary>Material decision audit event; must be written in the same transaction as the change.</summary>
public sealed record AuditEvent(Guid Id, DateTimeOffset At, Actor Actor, string EventType, string SubjectKind, string SubjectId, string? Reason);
