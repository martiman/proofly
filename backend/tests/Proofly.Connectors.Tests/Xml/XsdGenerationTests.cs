using Proofly.Connectors.Schemas;
using Proofly.Connectors.Xml;

namespace Proofly.Connectors.Tests.Xml;

public class XsdGenerationTests
{
    /// <summary>Set PROOFLY_REGEN_XSD=1 and run this test to rewrite the embedded XSD files from the generator.</summary>
    private static string XsdDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Proofly.sln"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("Proofly.sln not found"), "src", "Proofly.Connectors", "Xml", "Xsd");
    }

    [Fact]
    public void Embedded_xsd_files_equal_the_generator_output_for_every_source_and_version()
    {
        var regen = Environment.GetEnvironmentVariable("PROOFLY_REGEN_XSD") == "1";
        if (regen)
            foreach (var s in SourceSchemas.All)
                foreach (var v in s.SupportedVersions)
                    File.WriteAllText(Path.Combine(XsdDir(), $"{s.SourceName}.{v}.xsd"), XsdGenerator.Generate(s, v));
        foreach (var s in SourceSchemas.All)
            foreach (var v in s.SupportedVersions)
            {
                var generated = XsdGenerator.Generate(s, v);
                Assert.Equal(generated, EmbeddedXsd.Read(s.SourceName, v).Replace("\r\n", "\n"));
            }
    }

    [Fact]
    public void Generator_is_deterministic_and_mentions_columns_required_flags_and_enums()
    {
        var a = XsdGenerator.Generate(SourceSchemas.TransferInstructions, "1.0");
        Assert.Equal(a, XsdGenerator.Generate(SourceSchemas.TransferInstructions, "1.0"));
        Assert.Contains("name=\"external_reference\" minOccurs=\"0\"", a);
        Assert.DoesNotContain("name=\"amount\" minOccurs", a);
        Assert.Contains("value=\"SUBMITTED\"", a);
        Assert.Contains("<xs:enumeration value=\"transfer_instructions\"", a);
        Assert.Contains("declared_total_gbp", XsdGenerator.Generate(SourceSchemas.DeliveryManifest, "1.0"));
    }

    [Fact]
    public void Namespaced_generation_sets_the_target_namespace()
    {
        Assert.Contains("targetNamespace=\"urn:test\"", XsdGenerator.Generate(SourceSchemas.TransferInstructions, "1.0", "urn:test"));
    }
}
