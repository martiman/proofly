using Proofly.Domain.Enums;

namespace Proofly.Data.Audit;

/// <summary>Typed actor. Type follows the contract's ActorType (USER, SYSTEM, SERVICE_ACCOUNT).</summary>
public sealed record AuditActor(ActorType Type, string? Id, string Name)
{
    public static AuditActor System(string name = "system") => new(ActorType.System, null, name);
}

/// <summary>
/// An event to append. Before/After are JSON summaries only: no secrets, no raw evidence, money as decimal strings
/// (numbers with exponents are rejected so the stored jsonb round-trips to the same canonical form).
/// </summary>
public sealed record AuditEventInput(
    string Type,
    AuditActor Actor,
    string SubjectKind,
    string SubjectId,
    string Summary,
    string? Reason = null,
    string? RunId = null,
    string? RuleVersion = null,
    string? RequestId = null,
    string? BeforeJson = null,
    string? AfterJson = null,
    string ChainId = AuditChains.Global);

public static class AuditChains
{
    public const string Global = "*";
}

public sealed record AuditEventRecord(
    long Id, DateTimeOffset At, string Type, ActorType ActorType, string? ActorId, string ActorName,
    string SubjectKind, string SubjectId, string? RunId, string Summary, string? Reason, string? RuleVersion,
    string? RequestId, string? BeforeJson, string? AfterJson, string ChainId, long ChainSeq,
    string? PreviousHash, string Hash);

public sealed record AuditChainBreak(string ChainId, long ChainSeq, long EventId, string Problem);

public sealed record AuditVerification(long EventsChecked, AuditChainBreak? FirstBreak)
{
    public bool IsValid => FirstBreak is null;
}

public sealed record AuditQuery(
    string? RunId = null, string? SubjectKind = null, string? SubjectId = null, string? Type = null,
    string? ActorId = null, ActorType? ActorType = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    string? Search = null, int Limit = 25, string? Cursor = null);

public sealed record AuditPage(IReadOnlyList<AuditEventRecord> Items, string? NextCursor, bool HasMore);
