using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Completeness;

public enum SequenceStatus { NotApplicable, Ok, Gap, Overlap }

/// <summary>Snapshot of one source delivery as seen by the completeness control.</summary>
/// <param name="ActualCount">Rows in the file, accepted AND rejected (same basis as the declared count).</param>
/// <param name="ActualTotals">Sum of control amounts over all rows (accepted and rejected), per currency.</param>
/// <param name="Fingerprint">Content fingerprint; the same non-empty fingerprint twice for one source is a duplicate delivery.</param>
/// <param name="SupersedesDeliveryId">Delivery this one corrects.</param>
public sealed record DeliveryInfo(
    string DeliveryId, string SourceName, DeliveryState State, string Fingerprint,
    int? DeclaredCount, int ActualCount, IReadOnlyList<Money> DeclaredTotals, IReadOnlyList<Money> ActualTotals,
    bool SchemaSupported, SequenceStatus Sequence, int RejectedCount,
    string? SupersedesDeliveryId, DateTimeOffset? ExpectedBy, DateTimeOffset? ReceivedAt);

/// <summary>A rejected source row (from ingestion), kept visible in the completeness denominator.</summary>
public sealed record RejectedRecord(string SourceName, string DeliveryId, int RowNumber, string? SourceRowId, string ReasonCode, string Message);

public sealed record CompletenessInput(
    DateOnly BusinessDate,
    IReadOnlyList<string> MandatorySources,
    IReadOnlyList<DeliveryInfo> Deliveries,
    IReadOnlyList<RejectedRecord> Rejections);

/// <summary>Rule parameters. Defaults follow the ingestion policy: count mismatch blocks, value mismatch qualifies.</summary>
public sealed record CompletenessOptions
{
    public string RuleVersion { get; init; } = "completeness-1.0";
    public bool CountMismatchBlocking { get; init; } = true;
    public bool ValueMismatchBlocking { get; init; } = false;
    public bool SequenceBlocking { get; init; } = true;
    public bool LateBlocking { get; init; } = false;
    public bool DuplicateDeliveryBlocking { get; init; } = false;
    /// <summary>Per-source overrides of the two mismatch flags (source name, ordinal).</summary>
    public IReadOnlyDictionary<string, bool> CountMismatchBlockingBySource { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, bool> ValueMismatchBlockingBySource { get; init; } = new Dictionary<string, bool>();
}

public static class CompletenessCodes
{
    public const string SourceMissing = "SOURCE_MISSING";
    public const string SourceLate = "SOURCE_LATE";
    public const string SchemaUnsupported = "SCHEMA_UNSUPPORTED";
    public const string DuplicateDelivery = "DUPLICATE_DELIVERY";
    public const string SequenceGap = "SEQUENCE_GAP";
    public const string CorrectionLinkMissing = "CORRECTION_LINK_MISSING";
    public const string DeliveryRejected = "DELIVERY_REJECTED";
    public const string DeliverySuperseded = "DELIVERY_SUPERSEDED";
    public const string CompletenessQualified = "COMPLETENESS_QUALIFIED";
}
