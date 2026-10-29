namespace Proofly.Acceptance.Tests.Findings;

/// <summary>The comparison itself, without a database: it must report missing, wrong-severity and unexpected findings and apply the documented lenient references.</summary>
public sealed class FindingsComparisonTests
{
    private static ExpectedFinding E(string id, string file, string row, string control, string reason, string sev) => new(id, file, row, control, reason, sev, "d", "e");
    private static ProducedException P(string control, string reason, string sev, string? file, string? row) => new(control, reason, sev, file, row, "t");
    private static readonly IReadOnlyList<AllowedExtra> None = [];

    [Fact]
    public void An_exact_reproduction_is_clean()
    {
        var c = FindingsComparison.Compare([E("F-1", "a.csv", "R1", "matching", "VALUE_MISMATCH", "MEDIUM")], [P("matching", "VALUE_MISMATCH", "MEDIUM", "a.csv", "R1")], None);
        Assert.True(c.IsClean, c.Report());
    }

    [Fact]
    public void Missing_wrong_severity_and_unexpected_findings_are_all_named_in_the_report()
    {
        var expected = new[] { E("F-1", "a.csv", "R1", "matching", "VALUE_MISMATCH", "MEDIUM"), E("F-2", "a.csv", "R2", "safeguarding", "SHORTFALL", "CRITICAL") };
        var produced = new[] { P("safeguarding", "SHORTFALL", "HIGH", "a.csv", "R2"), P("matching", "NO_CANDIDATE", "MEDIUM", "a.csv", "R9") };
        var c = FindingsComparison.Compare(expected, produced, None);
        Assert.False(c.IsClean);
        Assert.Equal(["F-1"], c.Missing.Select(m => m.FindingId));
        Assert.Equal(["F-2"], c.SeverityMismatches.Select(m => m.Expected.FindingId));
        Assert.Single(c.Unexpected);
        var report = c.Report();
        Assert.Contains("MISSING    F-1", report);
        Assert.Contains("SEVERITY   F-2", report);
        Assert.Contains("UNEXPECTED matching NO_CANDIDATE", report);
    }

    [Fact]
    public void The_allow_list_explains_extra_exceptions_but_only_with_the_same_severity()
    {
        var allowed = new[] { new AllowedExtra("committed", "matching", "NO_CANDIDATE", "MEDIUM", "a.csv", "R9", "basis") };
        Assert.True(FindingsComparison.Compare([], [P("matching", "NO_CANDIDATE", "MEDIUM", "a.csv", "R9")], allowed).IsClean);
        Assert.False(FindingsComparison.Compare([], [P("matching", "NO_CANDIDATE", "HIGH", "a.csv", "R9")], allowed).IsClean);
    }

    [Theory]
    [InlineData("a.csv", "MULTIPLE", "a.csv", "R7", true)]
    [InlineData("a.csv", "CA-004", "a.csv", "CA-004|DAILY_ROLLFORWARD|2026-10-27", true)]
    [InlineData("a.csv", "CA-004", "a.csv", "CA-0045", false)]
    [InlineData("a.csv", "R1", "b.csv", "R1", false)]
    [InlineData("delivery_manifest.csv", "banking_transactions", "banking_transactions.csv", null, true)]
    [InlineData("delivery_manifest.csv", "banking_transactions", "banking_transactions.csv", "BTX-001", false)]
    public void Source_references_are_compared_as_documented(string file, string row, string producedFile, string? producedRow, bool expected) =>
        Assert.Equal(expected, FindingsComparison.Refers(file, row, producedFile, producedRow));

    [Fact]
    public void Several_exceptions_may_explain_one_expected_finding()
    {
        var c = FindingsComparison.Compare([E("F-1", "a.csv", "R1", "matching", "VALUE_MISMATCH", "MEDIUM")],
            [P("matching", "VALUE_MISMATCH", "MEDIUM", "a.csv", "R1"), P("matching", "VALUE_MISMATCH", "MEDIUM", "a.csv", "R1")], None);
        Assert.True(c.IsClean);
        Assert.Equal(2, c.Reproduced.Single().Count);
    }
}
