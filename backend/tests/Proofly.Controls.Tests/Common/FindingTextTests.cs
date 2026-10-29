using Proofly.Controls.Common;
using Proofly.Domain;
using Proofly.Domain.Enums;

namespace Proofly.Controls.Tests.Common;

public class FindingTextTests
{
    private static Finding F(string code, string description = "Some technical message that explains in detail what the control saw", Money? value = null, params string[] rows) =>
        new($"k:{code}", ControlArea.IndependentLedger, code, Severity.High, description, rows.Select(r => new RecordRef("src", r)).ToList(), value, "v1", true);

    [Fact]
    public void Headline_names_the_currency_reason_and_subject()
    {
        Assert.Equal("EUR balance difference on CA-004", FindingText.Headline(F(FindingCodes.BalanceDifference, value: new Money(12.5m, "EUR"), rows: "CA-004")));
        Assert.Equal("GBP safeguarding shortfall", FindingText.Headline(F(FindingCodes.Shortfall, value: new Money(-1m, "GBP"), rows: "MULTIPLE")));
        Assert.Equal("Required field missing on SRC-T-009", FindingText.Headline(F(FindingCodes.RequiredFieldMissing, rows: "SRC-T-009")));
        Assert.Equal("Duplicate record", FindingText.Headline(F(FindingCodes.DuplicateRecord)));
        Assert.Equal("EUR balance difference on A3", FindingText.Headline(F(FindingCodes.BalanceDifference, value: new Money(-40.5m, "EUR"), rows: "A3|DAILY_ROLLFORWARD|2026-10-27")));
    }

    [Fact]
    public void Headline_is_short_for_every_known_reason_code_and_for_an_unknown_one()
    {
        var codes = typeof(FindingCodes).GetFields().Where(x => x.IsLiteral).Select(x => (string)x.GetRawConstantValue()!)
            .Concat(["EXTERNAL_BALANCE_MISSING", "NO_CANDIDATE", "MISSING_INTERNAL_POSTING", "COMPLETENESS_QUALIFIED", "SOMETHING_NEW"]);
        foreach (var code in codes)
        {
            var h = FindingText.Headline(F(code, value: new Money(1m, "GBP"), rows: "A-VERY-LONG-SOURCE-ROW-IDENTIFIER-THAT-GOES-ON-AND-ON-AND-ON-AND-ON-AND-ON-AND-ON-1234567890"));
            Assert.InRange(h.Length, 1, FindingText.MaxHeadline);
            Assert.DoesNotContain("_", h);
        }
        Assert.Equal("Something new", FindingText.Label("SOMETHING_NEW"));
    }

    [Fact]
    public void Detail_is_longer_than_the_headline_and_adds_control_value_and_records()
    {
        var f = F(FindingCodes.BalanceDifference, "Independent balance differs from the recorded balance", new Money(12.5m, "EUR"), "CA-004", "CA-005");
        var detail = FindingText.Detail(f);
        Assert.StartsWith("Independent balance differs from the recorded balance.", detail);
        Assert.Contains("independent ledger control with reason balance difference", detail);
        Assert.Contains("12.50 EUR", detail);
        Assert.Contains("CA-004, CA-005", detail);
        Assert.True(detail.Length > FindingText.Headline(f).Length);
        Assert.NotEqual(f.Description, detail);
    }

    [Fact]
    public void Text_is_deterministic_and_lists_at_most_five_records()
    {
        var f = F(FindingCodes.ValueMismatch, value: new Money(1m, "GBP"), rows: Enumerable.Range(1, 8).Select(i => $"R-{i}").ToArray());
        Assert.Equal(FindingText.Detail(f), FindingText.Detail(f));
        Assert.Contains("R-1, R-2, R-3, R-4, R-5 and 3 more", FindingText.Detail(f));
        Assert.DoesNotContain("R-6,", FindingText.Detail(f));
    }
}
