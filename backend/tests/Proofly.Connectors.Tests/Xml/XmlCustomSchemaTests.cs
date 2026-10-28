using Proofly.Domain.Records;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Xml;

namespace Proofly.Connectors.Tests.Xml;

public class XmlCustomSchemaTests : IDisposable
{
    private const string S = SourceNames.TransferInstructions;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-xsd-" + Guid.NewGuid().ToString("N"));

    public XmlCustomSchemaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string xsd)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xsd");
        File.WriteAllText(p, xsd);
        return p;
    }

    // customer rule: instruction ids look like TRF-<digits>; the root requires a "batch" attribute
    private const string Custom = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">
          <xs:element name="instruction_id"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="TRF-[0-9]+"/></xs:restriction></xs:simpleType></xs:element>
          <xs:element name="delivery"><xs:complexType><xs:sequence>
            <xs:element name="record" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
              <xs:any processContents="lax" maxOccurs="unbounded"/></xs:sequence></xs:complexType></xs:element>
          </xs:sequence>
          <xs:anyAttribute processContents="skip"/></xs:complexType></xs:element>
        </xs:schema>
        """;

    [Fact]
    public async Task Custom_xsd_is_applied_in_addition_to_the_builtin_one_and_rejects_the_record_with_a_line()
    {
        var o = new XmlSourceOptions { CustomSchemaPath = Write(Custom) };
        var good = X.RecordXml(S, Samples.Row(S));
        var bad = X.RecordXml(S, Samples.Row(S).With("instruction_id", "ABC-1"));
        var res = await X.Parse(S, X.Delivery(S, good + "\n" + bad + "\n" + good), o);
        Assert.Equal([true, false, true], res.Select(r => r.IsAccepted));
        Assert.Equal(ReasonCodes.InvalidValue, res[1].Rejection!.ReasonCode);
        Assert.Equal("instruction_id", res[1].Rejection!.Field);
        Assert.Contains("line 4", res[1].Rejection!.Message);
        Assert.Contains("ABC-1", res[1].Rejection!.RawExcerpt);
        // the built-in XSD still applies
        var builtin = X.RecordXml(S, Samples.Row(S)).Replace("<currency>GBP</currency>", "<currency>gbp</currency>");
        Assert.Equal(ReasonCodes.InvalidValue, Assert.Single(await X.Parse(S, X.Delivery(S, builtin), o)).Rejection!.ReasonCode);
    }

    [Fact]
    public async Task Custom_xsd_root_violation_is_a_file_level_failure_with_position()
    {
        var strict = Custom.Replace("<xs:anyAttribute processContents=\"skip\"/>", "<xs:attribute name=\"batch\" use=\"required\"/><xs:anyAttribute processContents=\"skip\"/>");
        var o = new XmlSourceOptions { CustomSchemaPath = Write(strict) };
        var xml = X.Xml(S, Samples.Row(S));
        var v = await X.Validate(S, xml, o: o);
        Assert.False(v.IsValid);
        Assert.Contains("batch", v.Message);
        var ex = await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, xml, o));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Missing_or_invalid_custom_xsd_is_a_configuration_error()
    {
        Assert.Throws<InvalidOperationException>(() => X.Adapter(S, new XmlSourceOptions { CustomSchemaPath = Path.Combine(_dir, "nope.xsd") }));
        Assert.Throws<InvalidOperationException>(() => X.Adapter(S, new XmlSourceOptions { CustomSchemaPath = Write("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"a\" type=\"missing\"/></xs:schema>") }));
        Assert.Throws<InvalidOperationException>(() => X.Adapter(S, new XmlSourceOptions { CustomSchemaPath = Write("not xml") }));
    }
}
