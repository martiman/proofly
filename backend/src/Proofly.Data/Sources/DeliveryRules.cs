namespace Proofly.Data.Sources;

/// <summary>
/// Single home of the delivery action rules. The read endpoint derives <c>allowedActions</c> from these, and the
/// commands re-check them inside their transaction, so the two can never drift apart.
/// </summary>
public static class DeliveryRules
{
    public const string DuplicateReasonCode = "DUPLICATE_DELIVERY";

    /// <summary>Retry: a rejected delivery that is not a duplicate, or a received delivery that was never ingested.</summary>
    public static bool CanRetryIngestion(string state, bool ingested, bool isDuplicate) =>
        !isDuplicate && (state == "REJECTED" || (state == "RECEIVED" && !ingested));

    /// <summary>Supersede: only a received or qualified delivery that has no correcting delivery yet.</summary>
    public static bool CanSupersede(string state, bool alreadySuperseded) =>
        state is "RECEIVED" or "QUALIFIED" && !alreadySuperseded;
}
