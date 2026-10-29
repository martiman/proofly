using System.Text;
using Proofly.Connectors.Csv;

namespace Proofly.Acceptance.Tests.Findings;

/// <summary>One row of expected_findings.csv.</summary>
public sealed record ExpectedFinding(
    string FindingId, string SourceFile, string SourceRowId, string Control, string ReasonCode, string Severity, string Description, string ExpectedEffect);

/// <summary>One exception the pipeline produced (the columns the comparison needs).</summary>
public sealed record ProducedException(string Control, string ReasonCode, string Severity, string? SourceFile, string? SourceRowId, string Title)
{
    public override string ToString() => $"{Control} {ReasonCode} {Severity} {SourceFile ?? "-"} {(string.IsNullOrEmpty(SourceRowId) ? "-" : SourceRowId)}  ({Title})";
}

/// <summary>One row of expected_extra_findings.csv: a finding the documented rules really produce for the dataset although expected_findings.csv does not list it.</summary>
public sealed record AllowedExtra(string Dataset, string Control, string ReasonCode, string Severity, string SourceFile, string SourceRowId, string Basis);

public sealed record SeverityMismatch(ExpectedFinding Expected, IReadOnlyList<ProducedException> Produced);

/// <summary>An expected finding the documented rules cannot produce for the committed dataset (dataset inconsistency, see the findings report).</summary>
public sealed record KnownGap(string Dataset, string FindingId, string Basis);

public sealed record Comparison(
    IReadOnlyList<ExpectedFinding> Missing, IReadOnlyList<SeverityMismatch> SeverityMismatches,
    IReadOnlyList<ProducedException> Unexpected, IReadOnlyList<ProducedException> Allowed, IReadOnlyList<(ExpectedFinding Expected, int Count)> Reproduced,
    IReadOnlyList<ExpectedFinding> KnownGaps, IReadOnlyList<ExpectedFinding> StaleGaps)
{
    public bool IsClean => Missing.Count == 0 && SeverityMismatches.Count == 0 && Unexpected.Count == 0 && StaleGaps.Count == 0;

    /// <summary>A readable diff: what is missing, what has the wrong severity and what was produced without being expected.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"expected findings reproduced: {Reproduced.Count}, missing: {Missing.Count}, severity mismatches: {SeverityMismatches.Count}, unexpected: {Unexpected.Count} (allow-listed: {Allowed.Count}), known gaps: {KnownGaps.Count}");
        foreach (var g in KnownGaps)
            sb.AppendLine($"  KNOWN GAP  {g.FindingId} {g.Control} {g.ReasonCode} {g.Severity} {g.SourceFile} {g.SourceRowId}  [not produced by the documented rules, see docs/testing/dataset-findings-report.md]");
        foreach (var g in StaleGaps)
            sb.AppendLine($"  STALE GAP  {g.FindingId} is now reproduced: remove it from known_dataset_gaps.csv");
        foreach (var m in Missing)
            sb.AppendLine($"  MISSING    {m.FindingId} {m.Control} {m.ReasonCode} {m.Severity} {m.SourceFile} {m.SourceRowId}  [{m.Description}]");
        foreach (var s in SeverityMismatches)
            sb.AppendLine($"  SEVERITY   {s.Expected.FindingId} {s.Expected.Control} {s.Expected.ReasonCode} {s.Expected.SourceFile} {s.Expected.SourceRowId}: expected {s.Expected.Severity}, produced {string.Join("/", s.Produced.Select(p => p.Severity).Distinct())}");
        foreach (var u in Unexpected)
            sb.AppendLine($"  UNEXPECTED {u}");
        return sb.ToString();
    }
}

/// <summary>
/// Compares the exceptions of a run with expected_findings.csv. An expected finding is reproduced when an exception has the same control, reason code and
/// severity and refers to the same source file and row. The reference is compared leniently in three documented ways: <c>MULTIPLE</c> matches any row of
/// the file; a natural identifier (<c>CA-004</c>) matches a row id that extends it (<c>CA-004|DAILY_ROLLFORWARD|2026-10-27</c>); a manifest finding
/// (<c>delivery_manifest.csv</c>, row = source name) matches the delivery-level exception of <c>&lt;source name&gt;.csv</c>. Every exception must be explained by an
/// expected finding or by the allow-list. Several exceptions may explain one finding (the same discrepancy seen through two reconciliation pairs).
/// </summary>
public static class FindingsComparison
{
    public const string Manifest = "delivery_manifest.csv";

    public static bool Refers(string file, string row, string? producedFile, string? producedRow)
    {
        producedRow ??= "";
        if (file == Manifest) return producedFile == row + ".csv" && producedRow.Length == 0;
        if (!string.Equals(file, producedFile, StringComparison.Ordinal)) return false;
        if (row == "MULTIPLE") return true;
        return producedRow == row || (row.Length > 0 && producedRow.StartsWith(row + "|", StringComparison.Ordinal));
    }

    public static Comparison Compare(IReadOnlyList<ExpectedFinding> expected, IReadOnlyList<ProducedException> produced, IReadOnlyList<AllowedExtra> allowed, IReadOnlySet<string>? knownGaps = null)
    {
        knownGaps ??= new HashSet<string>();
        var missing = new List<ExpectedFinding>();
        var severity = new List<SeverityMismatch>();
        var reproduced = new List<(ExpectedFinding, int)>();
        var gaps = new List<ExpectedFinding>();
        var stale = new List<ExpectedFinding>();
        var explained = new HashSet<ProducedException>();
        foreach (var e in expected)
        {
            var sameKey = produced.Where(p => p.Control == e.Control && p.ReasonCode == e.ReasonCode && Refers(e.SourceFile, e.SourceRowId, p.SourceFile, p.SourceRowId)).ToList();
            var exact = sameKey.Where(p => p.Severity == e.Severity).ToList();
            if (knownGaps.Contains(e.FindingId))
            {
                if (exact.Count > 0) { stale.Add(e); reproduced.Add((e, exact.Count)); foreach (var p in exact) explained.Add(p); }
                else gaps.Add(e);
            }
            else if (exact.Count > 0) { reproduced.Add((e, exact.Count)); foreach (var p in exact) explained.Add(p); }
            else if (sameKey.Count > 0) { severity.Add(new SeverityMismatch(e, sameKey)); foreach (var p in sameKey) explained.Add(p); } // reported once, as a severity mismatch
            else missing.Add(e);
        }
        var allowedHits = new List<ProducedException>();
        var unexpected = new List<ProducedException>();
        foreach (var p in produced)
        {
            if (explained.Contains(p)) continue;
            if (allowed.Any(a => a.Control == p.Control && a.ReasonCode == p.ReasonCode && a.Severity == p.Severity && Refers(a.SourceFile, a.SourceRowId, p.SourceFile, p.SourceRowId)))
                allowedHits.Add(p);
            else unexpected.Add(p);
        }
        return new Comparison(missing, severity, unexpected, allowedHits, reproduced, gaps, stale);
    }

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false));
        var r = new CsvRecordReader(reader, ',');
        var rows = new List<Dictionary<string, string>>();
        List<string>? header = null;
        while (r.ReadAsync().AsTask().GetAwaiter().GetResult() is { } rec)
        {
            if (rec.IsBlank) continue;
            if (header is null) { header = rec.Fields.ToList(); continue; }
            if (rec.Fields.Count != header.Count) throw new InvalidDataException($"{path}: row has {rec.Fields.Count} cells, header has {header.Count}: {rec.Raw}");
            rows.Add(header.Zip(rec.Fields).ToDictionary(x => x.First, x => x.Second));
        }
        return rows;
    }

    public static List<ExpectedFinding> ReadExpected(string path) => ReadCsv(path).Select(r => new ExpectedFinding(
        r["finding_id"], r["source_file"], r["source_row_id"], r["control"], r["reason_code"], r["severity"], r["description"], r["expected_effect"])).ToList();

    public static List<KnownGap> ReadGaps(string path) => !File.Exists(path) ? [] : ReadCsv(path).Select(r => new KnownGap(r["dataset"], r["finding_id"], r["basis"])).ToList();

    public static List<AllowedExtra> ReadAllowed(string path) => !File.Exists(path) ? [] : ReadCsv(path).Select(r => new AllowedExtra(
        r["dataset"], r["control"], r["reason_code"], r["severity"], r["source_file"], r["source_row_id"], r["basis"])).ToList();
}
