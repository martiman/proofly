using System.Globalization;
using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Completeness;

/// <summary>
/// Data completeness control (docs/controls/data-completeness.md). Pure and deterministic. An incomplete population
/// never yields Passed: blocking findings give Failed, any other qualifying finding gives Qualified. A superseded
/// delivery whose correction is present is informational (Low) and does not qualify the population.
/// </summary>
public static class DataCompletenessControl
{
    public static ControlResult Evaluate(CompletenessInput input, CompletenessOptions? options = null)
    {
        var o = options ?? new CompletenessOptions();
        var f = new List<Finding>();
        var ds = input.Deliveries;
        string Bd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Finding Make(string key, string code, Severity sev, bool blocking, string desc, IEnumerable<RecordRef> recs, Money? value = null) =>
            new(key, ControlArea.DataCompleteness, code, sev, desc, recs.ToList(), value, o.RuleVersion, blocking);

        // Missing / rejected mandatory sources.
        foreach (var src in input.MandatorySources.Distinct(StringComparer.Ordinal))
        {
            var forSrc = ds.Where(d => d.SourceName == src).ToList();
            var accepted = forSrc.Any(d => d.State is DeliveryState.Received or DeliveryState.Late or DeliveryState.Qualified);
            if (accepted) continue;
            var rejected = forSrc.Where(d => d.State == DeliveryState.Rejected).OrderBy(d => d.DeliveryId, StringComparer.Ordinal).ToList();
            if (rejected.Count > 0)
                f.Add(Make($"delivery:rejected:{src}", CompletenessCodes.DeliveryRejected, Severity.High, true,
                    $"Mandatory source {src} has only rejected deliveries for {Bd(input.BusinessDate)}; the population is unusable.",
                    rejected.Select(d => new RecordRef(src, d.DeliveryId))));
            else
                f.Add(Make($"delivery:missing:{src}", CompletenessCodes.SourceMissing, Severity.High, true,
                    $"Mandatory source {src} has no accepted delivery for {Bd(input.BusinessDate)}; the run is incomplete.",
                    [new RecordRef(src, "-")]));
        }

        // Duplicate deliveries: same source and fingerprint; the earliest (received, then id) is the original.
        var dupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in ds.Where(d => !string.IsNullOrEmpty(d.Fingerprint)).GroupBy(d => (d.SourceName, d.Fingerprint)))
        {
            var ordered = g.OrderBy(d => d.ReceivedAt ?? DateTimeOffset.MaxValue).ThenBy(d => d.DeliveryId, StringComparer.Ordinal).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                dupIds.Add(ordered[i].DeliveryId);
                f.Add(Make($"delivery:duplicate:{g.Key.SourceName}:{ordered[i].DeliveryId}", CompletenessCodes.DuplicateDelivery,
                    Severity.Medium, o.DuplicateDeliveryBlocking,
                    $"Delivery {ordered[i].DeliveryId} of {g.Key.SourceName} has the same content as {ordered[0].DeliveryId}; it must not add records again.",
                    [new RecordRef(g.Key.SourceName, ordered[i].DeliveryId), new RecordRef(g.Key.SourceName, ordered[0].DeliveryId)]));
            }
        }

        var corrected = ds.Where(d => d.SupersedesDeliveryId != null).Select(d => d.SupersedesDeliveryId!).ToHashSet(StringComparer.Ordinal);

        foreach (var d in ds.OrderBy(x => x.SourceName, StringComparer.Ordinal).ThenBy(x => x.DeliveryId, StringComparer.Ordinal))
        {
            var r = new RecordRef(d.SourceName, d.DeliveryId);
            var k = $"{d.SourceName}:{d.DeliveryId}";

            if (d.State == DeliveryState.Superseded)
            {
                if (corrected.Contains(d.DeliveryId))
                    f.Add(Make($"delivery:superseded:{k}", CompletenessCodes.DeliverySuperseded, Severity.Low, false,
                        $"Delivery {d.DeliveryId} of {d.SourceName} was superseded by a correction; it is kept as evidence but excluded from checks.", [r]));
                else
                    f.Add(Make($"delivery:correctionlink:{k}", CompletenessCodes.CorrectionLinkMissing, Severity.High, true,
                        $"Delivery {d.DeliveryId} of {d.SourceName} is superseded but no delivery identifies it as corrected.", [r]));
                continue;
            }
            if (d.SupersedesDeliveryId != null && !ds.Any(x => x.DeliveryId == d.SupersedesDeliveryId))
                f.Add(Make($"delivery:correctionlink:{k}", CompletenessCodes.CorrectionLinkMissing, Severity.Medium, false,
                    $"Delivery {d.DeliveryId} corrects {d.SupersedesDeliveryId}, which is not a known delivery.", [r]));

            if (d.State == DeliveryState.Rejected || d.State == DeliveryState.Missing) continue; // reported at source level

            if (d.State == DeliveryState.Late || (d.ExpectedBy is { } e && d.ReceivedAt is { } rc && rc > e))
                f.Add(Make($"delivery:late:{k}", CompletenessCodes.SourceLate, Severity.Medium, o.LateBlocking,
                    $"Delivery {d.DeliveryId} of {d.SourceName} arrived after its expected time.", [r]));

            if (!d.SchemaSupported)
                f.Add(Make($"delivery:schema:{k}", CompletenessCodes.SchemaUnsupported, Severity.High, true,
                    $"Delivery {d.DeliveryId} of {d.SourceName} declares an unsupported schema version.", [r]));

            if (d.Sequence is SequenceStatus.Gap or SequenceStatus.Overlap)
                f.Add(Make($"delivery:sequence:{k}", CompletenessCodes.SequenceGap, Severity.High, o.SequenceBlocking,
                    $"Delivery {d.DeliveryId} of {d.SourceName} has a sequence {(d.Sequence == SequenceStatus.Gap ? "gap" : "overlap")}.", [r]));

            if (dupIds.Contains(d.DeliveryId)) continue; // duplicate content: counts/totals are not re-asserted

            if (d.DeclaredCount is { } dc && dc != d.ActualCount)
                f.Add(Make($"control:count:{k}", FindingCodes.ControlCountMismatch, Severity.High,
                    o.CountMismatchBlockingBySource.GetValueOrDefault(d.SourceName, o.CountMismatchBlocking),
                    $"Delivery {d.DeliveryId} of {d.SourceName} declares {dc} records but contains {d.ActualCount}.", [r]));

            if (d.DeclaredTotals.Count > 0)
            {
                var declared = Sum(d.DeclaredTotals);
                var actual = Sum(d.ActualTotals);
                foreach (var ccy in declared.Keys.Union(actual.Keys).OrderBy(c => c, StringComparer.Ordinal))
                {
                    var dv = declared.GetValueOrDefault(ccy);
                    var av = actual.GetValueOrDefault(ccy);
                    if (dv == av) continue;
                    f.Add(Make($"control:value:{k}:{ccy}", FindingCodes.ControlValueMismatch, Severity.High,
                        o.ValueMismatchBlockingBySource.GetValueOrDefault(d.SourceName, o.ValueMismatchBlocking),
                        $"Delivery {d.DeliveryId} of {d.SourceName} declares {dv.ToString(CultureInfo.InvariantCulture)} {ccy} but the rows total {av.ToString(CultureInfo.InvariantCulture)} {ccy}.",
                        [r], new Money(av - dv, ccy)));
                }
            }
        }

        // Rejected records stay visible, one finding per record.
        foreach (var x in input.Rejections)
        {
            var id = x.SourceRowId ?? $"row{x.RowNumber.ToString(CultureInfo.InvariantCulture)}";
            f.Add(Make($"record:{x.ReasonCode}:{x.SourceName}:{x.DeliveryId}:{id}", x.ReasonCode, Severity.High, false,
                $"Row {x.RowNumber} ({id}) of {x.SourceName} was rejected: {x.Message}. The population is qualified.",
                [new RecordRef(x.SourceName, id)]));
        }

        var list = f.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        var state = list.Any(x => x.Blocking) ? ControlState.Failed
            : list.Any(x => x.ReasonCode != CompletenessCodes.DeliverySuperseded) ? ControlState.Qualified
            : ControlState.Passed;
        return new ControlResult(ControlArea.DataCompleteness, state, list, o.RuleVersion);
    }

    /// <summary>
    /// A downstream control can never pass unqualified on an incomplete population: Passed becomes Qualified with an
    /// explicit finding. Other states are kept.
    /// </summary>
    public static ControlResult ApplyGate(ControlResult downstream, ControlResult completeness)
    {
        if (completeness.State == ControlState.Passed || downstream.State != ControlState.Passed) return downstream;
        var refs = completeness.Findings.Where(x => x.ReasonCode != CompletenessCodes.DeliverySuperseded)
            .SelectMany(x => x.Records).Distinct().OrderBy(x => x.SourceName, StringComparer.Ordinal).ThenBy(x => x.SourceRowId, StringComparer.Ordinal).ToList();
        var gate = new Finding($"completeness:gate:{downstream.Area}", downstream.Area, CompletenessCodes.CompletenessQualified,
            Severity.Medium,
            $"Completeness is {completeness.State}; the {downstream.Area} result cannot be an unqualified pass because the input population is incomplete or qualified.",
            refs, null, completeness.RuleVersion, false);
        var list = downstream.Findings.Append(gate).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        return downstream with { State = ControlState.Qualified, Findings = list };
    }

    private static Dictionary<string, decimal> Sum(IEnumerable<Money> m) =>
        m.GroupBy(x => x.Currency, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount), StringComparer.Ordinal);
}
