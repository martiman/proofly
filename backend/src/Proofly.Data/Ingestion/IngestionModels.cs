using Proofly.Connectors.Abstractions;
using Proofly.Data.Audit;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Ingestion;

public static class IngestionJobs
{
    /// <summary>Job type of <see cref="IngestDeliveryJob"/>.</summary>
    public const string IngestDelivery = "delivery.ingest";
}

public sealed record IngestDeliveryPayload(string DeliveryId);

public enum MismatchOutcome { Qualify, Reject }

/// <summary>
/// How declared-vs-actual control mismatches are classified for one source. Defaults: a record COUNT mismatch REJECTS
/// the delivery (the population is not what the source declared, so completeness cannot be asserted; a corrected
/// delivery is expected), a declared VALUE total mismatch QUALIFIES it (records are kept, the discrepancy is
/// recorded and visible). Row-level rejections always QUALIFY. Schema/evidence/file failures always REJECT.
/// </summary>
public sealed class SourcePolicy
{
    public MismatchOutcome CountMismatch { get; set; } = MismatchOutcome.Reject;
    public MismatchOutcome ValueMismatch { get; set; } = MismatchOutcome.Qualify;
}

public sealed class IngestionOptions
{
    public SourcePolicy Default { get; } = new();
    /// <summary>Per-source overrides keyed by manifest source name.</summary>
    public Dictionary<string, SourcePolicy> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sources that must have an ingested delivery for each business date.</summary>
    public IReadOnlyList<string> MandatorySources { get; set; } = SourceNames.DataSources;
    public SourcePolicy PolicyFor(string source) => Sources.GetValueOrDefault(source) ?? Default;
}

/// <summary>A delivery to register: the parsed manifest row plus the stored evidence identity.</summary>
/// <param name="Manifest">Manifest entry (declared count/totals, schema version, correction link, sequence).</param>
/// <param name="FingerprintSha256">SHA-256 of the stored file (<see cref="EvidenceObject.Sha256"/>).</param>
public sealed record DeliveryRegistration(
    DeliveryManifestEntry Manifest, string FingerprintSha256, SourceFormat Format, string LegalEntityId,
    string? RunId = null, AuditActor? Actor = null, string? RequestId = null);

public sealed record DeliveryFailure(string ReasonCode, string Message);

/// <param name="Created">False when the same delivery id with the same fingerprint was already registered (idempotent replay).</param>
/// <param name="IdConflict">Same delivery id but different content: nothing is stored; the conflict is audited.</param>
public sealed record RegistrationResult(
    string DeliveryId, DeliveryState? State, bool Created, bool IdConflict, IReadOnlyList<DeliveryFailure> Failures);

public sealed record MissingSource(string SourceName, string ReasonCode, string Message);

public sealed record IngestionOutcome(
    string DeliveryId, DeliveryState State, bool AlreadyIngested, int AcceptedCount, int RejectedCount,
    IReadOnlyList<DeliveryFailure> Failures);

/// <summary>Read model of source_deliveries for tests and callers (not the API DTO).</summary>
public sealed record DeliverySnapshot(
    string Id, string SourceName, DateOnly BusinessDate, DeliveryState State, string Fingerprint, string? SupersedesDeliveryId,
    int AcceptedRecordCount, int RejectedRecordCount, int? TotalRowCount, DateTimeOffset? IngestedAt, SourceFormat Format,
    string? LegalEntityId, string? RunId, int Version);

internal static class DeliveryStates
{
    public static string Wire(DeliveryState s) => WireNames.ToUpperSnake(s);
    public static DeliveryState Parse(string s) => WireEnum.TryParse<DeliveryState>(s, out var v) ? v : throw new InvalidOperationException($"Unknown delivery state {s}.");
}
