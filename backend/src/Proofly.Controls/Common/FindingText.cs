using System.Globalization;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Common;

/// <summary>
/// Human text of a finding for the exception queue. Pure and deterministic (no clock, no ids): the same finding always gives the same text.
/// <see cref="Headline"/> is the short title (at most <see cref="MaxHeadline"/> characters, e.g. "EUR balance difference on CA-004");
/// <see cref="Detail"/> is the longer description (the control's message plus where it was raised). Neither feeds the result hash.
/// </summary>
public static class FindingText
{
    public const int MaxHeadline = 80;
    private const int MaxListedRecords = 5;

    /// <summary>Label per reason code; {ccy} is replaced by the currency of the finding's value (dropped when the finding has no value).</summary>
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [FindingCodes.RequiredFieldMissing] = "Required field missing",
        [FindingCodes.InvalidValue] = "Invalid value",
        [FindingCodes.DuplicateRecord] = "Duplicate record",
        [FindingCodes.ControlCountMismatch] = "Control count mismatch",
        [FindingCodes.ControlValueMismatch] = "{ccy} control total mismatch",
        [FindingCodes.ValueMismatch] = "{ccy} value mismatch",
        [FindingCodes.BalanceDifference] = "{ccy} balance difference",
        [FindingCodes.InvalidSign] = "Invalid sign",
        [FindingCodes.InvalidState] = "Invalid state",
        [FindingCodes.MissingInstruction] = "Missing instruction",
        [FindingCodes.MissingAuthorization] = "Missing authorisation",
        [FindingCodes.MissingOriginalTransaction] = "Missing original transaction",
        [FindingCodes.MissingSettlement] = "Missing settlement",
        [FindingCodes.ReturnNotPosted] = "Return not posted",
        [FindingCodes.Shortfall] = "{ccy} safeguarding shortfall",
        [FindingCodes.ExternalBalanceStale] = "{ccy} external balance stale",
        ["EXTERNAL_BALANCE_MISSING"] = "{ccy} external balance missing",
        ["ADJUSTMENT_UNAPPROVED"] = "{ccy} adjustment not approved",
        ["NO_CANDIDATE"] = "{ccy} no matching counterpart",
        ["AMBIGUOUS_CANDIDATES"] = "{ccy} ambiguous candidates",
        ["CURRENCY_MISMATCH"] = "Currency mismatch",
        ["PARTIAL_MATCH"] = "{ccy} partial match",
        ["OPENING_BALANCE_MISSING"] = "Opening balance missing",
        ["OPENING_BALANCE_UNSUPPORTED"] = "Opening balance unsupported",
        ["MOVEMENT_UNMAPPED"] = "Unmapped movement",
        ["MOVEMENT_MISSING"] = "Missing movement",
        ["INVALID_VALUE_DATE"] = "Invalid value date",
        ["RECORDED_BALANCE_MISSING"] = "Recorded balance missing",
        ["CALCULATION_SCOPE_MISMATCH"] = "Calculation scope mismatch",
        ["INVALID_TRANSITION"] = "Invalid lifecycle transition",
        ["MISSING_INTERNAL_POSTING"] = "Missing internal posting",
        ["MISSING_EXTERNAL_OUTCOME"] = "Missing external outcome",
        ["STALLED"] = "Stalled transaction",
        ["LATE_OUTCOME"] = "Late outcome",
        ["REVERSAL_NOT_POSTED"] = "Reversal not posted",
        ["CONFLICTING_OUTCOMES"] = "Conflicting outcomes",
        ["SOURCE_MISSING"] = "Source missing",
        ["SOURCE_LATE"] = "Source late",
        ["SCHEMA_UNSUPPORTED"] = "Unsupported schema",
        ["DUPLICATE_DELIVERY"] = "Duplicate delivery",
        ["SEQUENCE_GAP"] = "Sequence gap",
        ["CORRECTION_LINK_MISSING"] = "Correction link missing",
        ["DELIVERY_REJECTED"] = "Delivery rejected",
        ["DELIVERY_SUPERSEDED"] = "Delivery superseded",
        ["COMPLETENESS_QUALIFIED"] = "Data completeness qualified",
    };

    /// <summary>Label of a reason code: the mapped text, or the code in sentence case when it is unknown.</summary>
    public static string Label(string reasonCode, string? currency = null)
    {
        if (!Labels.TryGetValue(reasonCode, out var label)) { return Sentence(reasonCode); }
        label = currency is null ? label.Replace("{ccy} ", "", StringComparison.Ordinal) : label.Replace("{ccy}", currency, StringComparison.Ordinal);
        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    /// <summary>Short human title, at most <see cref="MaxHeadline"/> characters: "{label} on {subject}" (the subject is the first record, when there is one).</summary>
    public static string Headline(Finding f)
    {
        var text = Label(f.ReasonCode, f.Value?.Currency);
        var subject = Subject(f);
        if (subject is not null) { text += " on " + subject; }
        return text.Length <= MaxHeadline ? text : text[..(MaxHeadline - 1)].TrimEnd() + "…";
    }

    /// <summary>Longer description: the control's own message followed by control, severity, value and the records involved.</summary>
    public static string Detail(Finding f)
    {
        var parts = new List<string>
        {
            f.Description.TrimEnd('.', ' ') + ".",
            $"Raised by the {Area(f.Area)} control with reason {Label(f.ReasonCode).ToLowerInvariant()} (severity {f.Severity.ToString().ToLowerInvariant()}{(f.Blocking ? ", blocks sign-off" : "")}).",
        };
        if (f.Value is { } v) { parts.Add($"Value at issue: {v.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {v.Currency}."); }
        if (f.Records.Count > 0)
        {
            var listed = string.Join(", ", f.Records.Take(MaxListedRecords).Select(r => r.SourceRowId));
            parts.Add(f.Records.Count > MaxListedRecords ? $"Records: {listed} and {f.Records.Count - MaxListedRecords} more." : $"Records: {listed}.");
        }
        return string.Join(' ', parts);
    }

    private static string? Subject(Finding f)
    {
        var first = f.Records.Select(r => r.SourceRowId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        // Composite row keys ("A3|DAILY_ROLLFORWARD|2026-10-27") are named by their first part, the account.
        if (first is not null && first.Contains('|')) { first = first[..first.IndexOf('|')]; }
        return string.IsNullOrWhiteSpace(first) || first.Equals("MULTIPLE", StringComparison.OrdinalIgnoreCase) ? null : first;
    }

    private static string Area(ControlArea area) => area switch
    {
        ControlArea.DataCompleteness => "data completeness",
        ControlArea.Matching => "matching",
        ControlArea.IndependentLedger => "independent ledger",
        ControlArea.TransactionLifecycle => "transaction lifecycle",
        _ => "safeguarding",
    };

    private static string Sentence(string code)
    {
        var s = code.Replace('_', ' ').ToLowerInvariant();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
