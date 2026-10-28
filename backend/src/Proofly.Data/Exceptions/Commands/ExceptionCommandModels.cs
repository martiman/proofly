namespace Proofly.Data.Exceptions.Commands;

public enum CommandStatus { Ok, NotFound, Stale, InvalidState, RunSuperseded, Invalid, LimitReached, Duplicate, Forbidden, StaleProposal }

/// <summary>Outcome of a command transaction. On anything but Ok the transaction was rolled back: no change and no audit event.</summary>
public sealed record CommandOutcome(CommandStatus Status, string? Detail = null, string? Pointer = null)
{
    public static readonly CommandOutcome Ok = new(CommandStatus.Ok);
}

/// <summary>Identity of an exception for authorisation and the If-Match check.</summary>
public sealed record ExceptionRef(string Id, string RunId, string LegalEntityId, int Version);

public sealed record CommandContext(string ActorUserId, string? RequestId);

public sealed record SavedViewRow(string Id, string OwnerId, string OwnerName, string Name, bool Shared, string FiltersJson, string? Sort, DateTimeOffset CreatedAt, int Version);
