using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Xml;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests.Xml;

public class XmlAdapterTests
{
    [Theory, MemberData(nameof(Samples.AllSources), MemberType = typeof(Samples))]
    public async Task Valid_record_is_accepted_and_equals_the_csv_mapping_for_every_source(string source)
    {
        Assert.True((await X.Validate(source, X.Xml(source, Samples.Row(source)))).IsValid);
        var xml = Assert.Single(await X.Parse(source, X.Xml(source, Samples.Row(source))));
        var csv = Assert.Single(await Samples.Parse(source, Samples.Csv(source, Samples.Row(source))));
        Assert.True(xml.IsAccepted, xml.Rejection?.Message);
        if (xml.Record is DeliveryManifestEntry xm)
        {
            var cm = (DeliveryManifestEntry)csv.Record!;
            Assert.Equal(cm.DeclaredTotals, xm.DeclaredTotals);
            Assert.Equal(cm with { DeclaredTotals = [] }, xm with { DeclaredTotals = [] });
        }
        else Assert.Equal(csv.Record, xml.Record); // same RowMapper, same dictionary
        Assert.Equal(1, xml.RowNumber);
    }

    [Fact]
    public async Task Money_is_decimal_with_currency_for_gbp_eur_jpy()
    {
        var s = SourceNames.TransferInstructions;
        foreach (var (amount, ccy) in new[] { ("100.50", "GBP"), ("20", "EUR"), ("5000", "JPY") })
        {
            var r = Assert.Single(await X.Parse(s, X.Xml(s, Samples.Row(s).With("amount", amount).With("currency", ccy))));
            Assert.Equal(new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), ccy), ((TransferInstruction)r.Record!).Amount);
        }
    }

    [Fact]
    public async Task Pretty_printed_whitespace_is_formatting_but_values_are_verbatim()
    {
        var s = SourceNames.TransferInstructions;
        var row = Samples.Row(s).With("client_account_id", " CA 1 ");
        var xml = X.Delivery(s, "  " + X.RecordXml(s, row, pretty: true));
        var r = Assert.Single(await X.Parse(s, xml));
        Assert.Equal(" CA 1 ", ((TransferInstruction)r.Record!).ClientAccountId);
    }

    [Fact]
    public async Task Whitespace_only_required_value_is_not_dropped_and_is_rejected_by_the_mapper()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await X.Parse(s, X.Xml(s, Samples.Row(s)).Replace("<client_account_id>CA-1</client_account_id>", "<client_account_id>   </client_account_id>")));
        Assert.Equal(ReasonCodes.RequiredFieldMissing, r.Rejection!.ReasonCode);
        Assert.Equal("client_account_id", r.Rejection.Field);
    }

    [Fact]
    public async Task Cdata_and_character_references_are_decoded()
    {
        var s = SourceNames.TransferInstructions;
        var xml = X.Xml(s, Samples.Row(s)).Replace("<destination_reference>B1</destination_reference>", "<destination_reference><![CDATA[B&1]]></destination_reference>")
            .Replace("<external_reference/>", "<external_reference>A&amp;B&#33;</external_reference>");
        var t = (TransferInstruction)Assert.Single(await X.Parse(s, xml)).Record!;
        Assert.Equal("B&1", t.DestinationReference);
        Assert.Equal("A&B!", t.ExternalReference);
    }

    [Fact]
    public async Task Blank_optional_element_and_absent_optional_element_are_both_absent()
    {
        var s = SourceNames.TransferInstructions;
        var blank = (TransferInstruction)Assert.Single(await X.Parse(s, X.Xml(s, Samples.Row(s)))).Record!;
        var absent = (TransferInstruction)Assert.Single(await X.Parse(s, X.Delivery(s, X.RecordXml(s, Samples.Row(s)).Replace("<external_reference/>", "")))).Record!;
        Assert.Null(blank.ExternalReference);
        Assert.Equal(blank, absent);
    }

    [Fact]
    public async Task Required_element_absent_is_REQUIRED_FIELD_MISSING_with_line_raw_and_control_amount_and_the_file_continues()
    {
        var s = SourceNames.TransferInstructions;
        var bad = X.RecordXml(s, Samples.Row(s)).Replace("<client_account_id>CA-1</client_account_id>", "");
        var good = X.RecordXml(s, Samples.Row(s).With("source_row_id", "S2").With("instruction_id", "TRF-2"));
        var res = await X.Parse(s, X.Delivery(s, bad + "\n" + good));
        Assert.Equal(2, res.Count);
        var rej = res[0].Rejection!;
        Assert.Equal(ReasonCodes.RequiredFieldMissing, rej.ReasonCode);
        Assert.Equal("client_account_id", rej.Field);
        Assert.Equal("S1", rej.SourceRowId);
        Assert.Equal(new Money(100.50m, "GBP"), rej.ControlAmount);
        Assert.Contains("line 3", rej.Message);
        Assert.Contains("TRF-1", rej.RawExcerpt);
        Assert.True(res[1].IsAccepted);
        Assert.Equal(2, res[1].RowNumber);
    }

    [Theory]
    [InlineData("<source_status>SUBMITTED</source_status>", "<source_status>submitted</source_status>")]
    [InlineData("<amount>100.50</amount>", "<amount>1,5</amount>")]
    [InlineData("<amount>100.50</amount>", "<amount>1.1234567</amount>")]
    [InlineData("<amount>100.50</amount>", "<amount currency=\"GBP\">100.50</amount>")]
    [InlineData("<currency>GBP</currency>", "<currency>gbp</currency>")]
    [InlineData("<requested_value_date>2026-10-27</requested_value_date>", "<requested_value_date>27.10.2026</requested_value_date>")]
    [InlineData("<instruction_timestamp>2026-10-27T08:15:00+01:00</instruction_timestamp>", "<instruction_timestamp>2026-10-27 08:15:00</instruction_timestamp>")]
    [InlineData("<currency>GBP</currency>", "<currency><x>GBP</x></currency>")]
    [InlineData("<currency>GBP</currency>", "<currency>GBP</currency><currency>GBP</currency>")]
    [InlineData("<currency>GBP</currency>", "<currency>GBP</currency><surprise>1</surprise>")]
    public async Task Xsd_violations_reject_only_that_record_as_INVALID_VALUE(string from, string to)
    {
        var s = SourceNames.TransferInstructions;
        var bad = X.RecordXml(s, Samples.Row(s)).Replace(from, to);
        Assert.NotEqual(X.RecordXml(s, Samples.Row(s)), bad);
        var res = await X.Parse(s, X.Delivery(s, bad + "\n" + X.RecordXml(s, Samples.Row(s))));
        Assert.Equal(2, res.Count);
        Assert.Equal(ReasonCodes.InvalidValue, res[0].Rejection!.ReasonCode);
        Assert.Contains("line 3", res[0].Rejection!.Message);
        Assert.NotNull(res[0].Rejection!.RawExcerpt);
        Assert.True(res[1].IsAccepted);
    }

    [Fact]
    public async Task Value_that_passes_xsd_but_fails_semantics_is_rejected_by_the_mapper_with_its_reason()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await X.Parse(s, X.Xml(s, Samples.Row(s).With("requested_value_date", "2026-02-30"))));
        Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode);
        Assert.Equal("requested_value_date", r.Rejection.Field);
    }

    [Fact]
    public async Task Too_long_text_is_rejected_with_the_field()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await X.Parse(s, X.Xml(s, Samples.Row(s).With("instruction_id", new string('x', 65)))));
        Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode);
        Assert.Equal("instruction_id", r.Rejection.Field);
    }

    [Fact]
    public async Task Manifest_accepts_declared_totals_for_any_iso_currency_and_rejects_unknown_ones()
    {
        var s = SourceNames.DeliveryManifest;
        var m = (DeliveryManifestEntry)Assert.Single(await X.Parse(s, X.Xml(s, Samples.Row(s)))).Record!;
        Assert.Equal(2, m.DeclaredTotals.Count);
        var bad = Assert.Single(await X.Parse(s, X.Delivery(s, X.RecordXml(s, Samples.Row(s)).Replace("</record>", "<declared_total_xyz>1</declared_total_xyz></record>"))));
        Assert.Equal(ReasonCodes.InvalidValue, bad.Rejection!.ReasonCode);
    }

    [Fact]
    public async Task Empty_delivery_yields_no_rows()
    {
        var s = SourceNames.TransferInstructions;
        Assert.Empty(await X.Parse(s, X.Delivery(s, "")));
        Assert.Empty(await X.Parse(s, $"<delivery source=\"{s}\" schemaVersion=\"1.0\" businessDate=\"2026-10-27\"/>"));
    }

    [Fact]
    public async Task Encoding_follows_the_xml_declaration_and_bom()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var s = SourceNames.TransferInstructions;
        var xml = X.Xml(s, Samples.Row(s).With("external_reference", "Žluťoučký"));
        var cp = Encoding.GetEncoding("windows-1250");
        var bytes = cp.GetBytes(xml.Replace("UTF-8", "windows-1250"));
        Assert.Equal("Žluťoučký", ((TransferInstruction)Assert.Single(await X.Parse(s, new MemoryStream(bytes))).Record!).ExternalReference);
        var bom = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();
        Assert.Equal("Žluťoučký", ((TransferInstruction)Assert.Single(await X.Parse(s, new MemoryStream(bom))).Record!).ExternalReference);
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml.Replace("UTF-8", "utf-16"))).ToArray();
        Assert.True(Assert.Single(await X.Parse(s, new MemoryStream(utf16))).IsAccepted);
    }

    [Fact]
    public async Task Namespace_option_requires_and_generates_a_namespaced_layout()
    {
        var s = SourceNames.TransferInstructions;
        var o = new XmlSourceOptions { Namespace = "urn:test:delivery" };
        var xml = X.Delivery(s, X.RecordXml(s, Samples.Row(s)), ns: "urn:test:delivery");
        Assert.True(Assert.Single(await X.Parse(s, xml, o)).IsAccepted);
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(s, X.Xml(s, Samples.Row(s)), o)); // missing namespace
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(s, xml)); // namespace not configured
    }

    [Fact]
    public async Task Module_registers_all_nine_xml_adapters()
    {
        using var sp = new ServiceCollection().AddXmlAdapters().BuildServiceProvider();
        var registry = new SourceAdapterRegistry(sp.GetServices<ISourceAdapter>());
        foreach (var s in SourceNames.DataSources.Append(SourceNames.DeliveryManifest))
            Assert.Contains("1.0", registry.Resolve(s, SourceFormat.Xml).SupportedSchemaVersions);
        Assert.Equal(9, registry.Adapters.Count);
        await Task.CompletedTask;
    }
}

public class XmlStructureTests
{
    private const string S = SourceNames.TransferInstructions;

    [Fact]
    public async Task Wrong_root_is_a_file_level_failure()
    {
        var xml = X.Delivery(S, "", root: "batch");
        var v = await X.Validate(S, xml);
        Assert.False(v.IsValid);
        Assert.Contains("<delivery>", v.Message);
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, xml));
    }

    [Theory]
    [InlineData("source=\"transfer_instructions\"", "source=\"direct_debit_events\"", "source")]
    [InlineData("schemaVersion=\"1.0\"", "schemaVersion=\"9.9\"", "not supported")]
    [InlineData("schemaVersion=\"1.0\"", "", "schemaVersion is missing")]
    [InlineData("businessDate=\"2026-10-27\"", "businessDate=\"2026-10-28\"", "businessDate")]
    [InlineData("businessDate=\"2026-10-27\"", "businessDate=\"27.10.2026\"", "businessDate")]
    public async Task Root_attribute_problems_are_file_level_failures(string from, string to, string expected)
    {
        var xml = X.Delivery(S, "").Replace(from, to);
        var v = await X.Validate(S, xml);
        Assert.False(v.IsValid);
        Assert.Contains(expected, v.Message);
        var ex = await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, xml));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public async Task Unsupported_descriptor_version_fails_validation()
    {
        var v = await X.Validate(S, X.Delivery(S, "", version: "2.0"), version: "2.0");
        Assert.False(v.IsValid);
        Assert.Equal(ReasonCodes.SchemaUnsupported, v.ReasonCode);
    }

    [Fact]
    public async Task Extra_root_attribute_and_foreign_root_child_fail_the_file()
    {
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, X.Delivery(S, "").Replace("businessDate=", "extra=\"1\" businessDate=")));
        var ex = await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, X.Delivery(S, "<row/>")));
        Assert.Contains("<row>", ex.Message);
    }

    [Fact]
    public async Task Malformed_xml_throws_with_line_and_position_after_earlier_rows_were_yielded()
    {
        var good = X.RecordXml(S, Samples.Row(S));
        var xml = X.Delivery(S, good + "\n" + good).Replace("</delivery>", "").Replace("</record>\n\n", "</recordX>\n");
        var list = new List<ParseResult<ISourceRecord>>();
        var ex = await Assert.ThrowsAsync<SourceFormatException>(async () =>
        {
            await foreach (var r in X.Adapter(S).ParseRecordsAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)), X.Descriptor(S))) list.Add(r);
        });
        Assert.Contains("line", ex.Message);
        Assert.Single(list);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml")]
    [InlineData("<delivery")]
    public async Task Garbage_input_throws_SourceFormatException(string text)
    {
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, text));
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Validate(S, text));
    }

    [Fact]
    public async Task Text_between_records_is_not_accepted()
    {
        await Assert.ThrowsAnyAsync<SourceFormatException>(() => X.Parse(S, X.Delivery(S, "hello")));
        var rec = Assert.Single(await X.Parse(S, X.Delivery(S, X.RecordXml(S, Samples.Row(S)).Replace("<record>", "<record>stray"))));
        Assert.Equal(ReasonCodes.InvalidValue, rec.Rejection!.ReasonCode);
    }
}
