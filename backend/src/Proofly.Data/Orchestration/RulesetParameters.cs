using System.Globalization;
using System.Text.Json;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Orchestration;

/// <summary>
/// Pure: applies the control parameters stored in a ruleset snapshot (<c>ruleset_versions.snapshot</c>, an array of
/// <c>{id, kind, name, version, payload}</c> taken from the enabled <c>config_rules</c>) on top of the code defaults.
/// Only <c>kind = rule</c> entries with the documented rule ids below are read; every other entry and every parameter
/// that is not configured keeps its code default. A stored value that does not parse is an error (the run fails
/// visibly), never silently replaced.
/// <list type="table">
/// <item><term>MATCH-AMOUNT-TOLERANCE</term><description>tolerance "amount CCY" -> Matching amount tolerance for that currency; severity -> value mismatch severity</description></item>
/// <item><term>MATCH-UNMATCHED</term><description>severity -> unmatched severity</description></item>
/// <item><term>MATCH-AMBIGUOUS</term><description>severity -> ambiguous-candidates severity</description></item>
/// <item><term>LEDGER-BALANCE-TOLERANCE</term><description>tolerance amount -> independent ledger balance tolerance</description></item>
/// <item><term>LIFECYCLE-DEFAULT</term><description>severity -> default lifecycle severity</description></item>
/// <item><term>LIFECYCLE-TIMING</term><description>severity -> overdue (timing) severity</description></item>
/// <item><term>SAFEGUARDING-STALE</term><description>severity -> stale balance severity</description></item>
/// </list>
/// </summary>
public static class RulesetParameters
{
    public static RunProcessingOptions Apply(RunProcessingOptions defaults, string? snapshotJson)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (string.IsNullOrWhiteSpace(snapshotJson)) return defaults;
        using var doc = JsonDocument.Parse(snapshotJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return defaults;

        var o = defaults;
        foreach (var item in doc.RootElement.EnumerateArray().OrderBy(i => Str(i, "id"), StringComparer.Ordinal))
        {
            if (Str(item, "kind") != "rule" || !item.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            var id = Str(item, "id");
            switch (id)
            {
                case "MATCH-AMOUNT-TOLERANCE":
                    if (Tolerance(id, p) is { } t)
                    {
                        var map = new Dictionary<string, decimal>(o.Matching.AmountTolerance) { [t.Currency] = t.Amount };
                        o = o with { Matching = o.Matching with { AmountTolerance = map } };
                    }
                    if (Sev(id, p) is { } vs) o = o with { Matching = o.Matching with { ValueMismatchSeverity = vs } };
                    break;
                case "MATCH-UNMATCHED":
                    if (Sev(id, p) is { } us) o = o with { Matching = o.Matching with { UnmatchedSeverity = us } };
                    break;
                case "MATCH-AMBIGUOUS":
                    if (Sev(id, p) is { } asv) o = o with { Matching = o.Matching with { AmbiguousSeverity = asv } };
                    break;
                case "LEDGER-BALANCE-TOLERANCE":
                    if (Tolerance(id, p) is { } lt) o = o with { Ledger = o.Ledger with { Tolerance = lt.Amount } };
                    break;
                case "LIFECYCLE-DEFAULT":
                    if (Sev(id, p) is { } ds) o = o with { Lifecycle = o.Lifecycle with { DefaultSeverity = ds } };
                    break;
                case "LIFECYCLE-TIMING":
                    if (Sev(id, p) is { } ts) o = o with { Lifecycle = o.Lifecycle with { TimingSeverity = ts } };
                    break;
                case "SAFEGUARDING-STALE":
                    if (Sev(id, p) is { } ss) o = o with { Safeguarding = o.Safeguarding with { StaleSeverity = ss } };
                    break;
            }
        }
        return o;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static (decimal Amount, string Currency)? Tolerance(string ruleId, JsonElement payload)
    {
        if (Str(payload, "tolerance") is not { } text) return null;
        var parts = text.Split(' ');
        if (parts.Length != 2 || parts[1].Length != 3
            || !decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            throw new InvalidOperationException($"Rule {ruleId}: tolerance '{text}' is not '<non-negative decimal> <CCY>'.");
        return (amount, parts[1]);
    }

    private static Severity? Sev(string ruleId, JsonElement payload)
    {
        if (Str(payload, "severity") is not { } text) return null;
        return WireEnum.TryParse<Severity>(text, out var s) ? s : throw new InvalidOperationException($"Rule {ruleId}: unknown severity '{text}'.");
    }
}
