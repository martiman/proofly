using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Common;

/// <summary>Reason codes shared by all controls (they map 1:1 to the reason_code column of expected_findings.csv).</summary>
public static class FindingCodes
{
    public const string RequiredFieldMissing = "REQUIRED_FIELD_MISSING";
    public const string InvalidValue = "INVALID_VALUE";
    public const string DuplicateRecord = "DUPLICATE_RECORD";
    public const string ControlCountMismatch = "CONTROL_COUNT_MISMATCH";
    public const string ControlValueMismatch = "CONTROL_VALUE_MISMATCH";
    public const string ValueMismatch = "VALUE_MISMATCH";
    public const string BalanceDifference = "BALANCE_DIFFERENCE";
    public const string InvalidSign = "INVALID_SIGN";
    public const string InvalidState = "INVALID_STATE";
    public const string MissingInstruction = "MISSING_INSTRUCTION";
    public const string MissingAuthorization = "MISSING_AUTHORIZATION";
    public const string MissingOriginalTransaction = "MISSING_ORIGINAL_TRANSACTION";
    public const string MissingSettlement = "MISSING_SETTLEMENT";
    public const string ReturnNotPosted = "RETURN_NOT_POSTED";
    public const string Shortfall = "SHORTFALL";
    public const string ExternalBalanceStale = "EXTERNAL_BALANCE_STALE";
}

/// <summary>A reference to a source record that supports or causes a finding.</summary>
public sealed record RecordRef(string SourceName, string SourceRowId);

/// <summary>
/// One deterministic control finding. <see cref="Primary"/> names the record the finding is about (the one an exception is attributed to:
/// source file and row); <see cref="Records"/> lists every supporting record in a stable but otherwise arbitrary order. Pure data: no clock, no ids generated at random.
/// <see cref="Key"/> is a stable identity (same inputs and rules always give the same key) so reprocessing is idempotent.
/// </summary>
public sealed record Finding(
    string Key,
    ControlArea Area,
    string ReasonCode,
    Severity Severity,
    string Description,
    IReadOnlyList<RecordRef> Records,
    Money? Value = null,
    string RuleVersion = "",
    bool Blocking = false,
    RecordRef? Primary = null);

/// <summary>Outcome of one control over one population.</summary>
public sealed record ControlResult(ControlArea Area, ControlState State, IReadOnlyList<Finding> Findings, string RuleVersion)
{
    public static ControlResult From(ControlArea area, string ruleVersion, IEnumerable<Finding> findings)
    {
        var list = findings.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var state = list.Any(f => f.Blocking) ? ControlState.Failed
            : list.Count > 0 ? ControlState.ReviewRequired : ControlState.Passed;
        return new ControlResult(area, state, list, ruleVersion);
    }
}
