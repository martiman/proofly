using System.Diagnostics;
using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Xml;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests.Xml;

public class XmlSecurityTests
{
    private const string S = SourceNames.TransferInstructions;

    private static string WithDoctype(string doctype, string externalRef = "&xxe;") =>
        $"<?xml version=\"1.0\"?>\n{doctype}\n<delivery source=\"{S}\" schemaVersion=\"1.0\" businessDate=\"2026-10-27\">" +
        X.RecordXml(S, Samples.Row(S).With("external_reference", "PLACEHOLDER")).Replace("PLACEHOLDER", externalRef) + "</delivery>";

    [Fact]
    public async Task External_entity_payload_is_rejected_without_reading_the_file()
    {
        var secret = Path.Combine(Path.GetTempPath(), "proofly-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xml = WithDoctype($"<!DOCTYPE delivery [<!ENTITY xxe SYSTEM \"file://{secret}\">]>");
            var ex = await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, xml));
            Assert.DoesNotContain("TOP-SECRET", ex.ToString());
            var v = await Assert.ThrowsAsync<SourceFormatException>(() => X.Validate(S, xml));
            Assert.DoesNotContain("TOP-SECRET", v.ToString());
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task Remote_dtd_and_parameter_entity_payloads_are_rejected_without_network_access()
    {
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, WithDoctype("<!DOCTYPE delivery SYSTEM \"http://127.0.0.1:1/evil.dtd\">", "x")));
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, WithDoctype("<!DOCTYPE delivery [<!ENTITY % p SYSTEM \"http://127.0.0.1:1/x\"> %p;]>", "x")));
    }

    [Fact]
    public async Task Billion_laughs_is_rejected_quickly_without_expansion()
    {
        var sb = new StringBuilder("<!DOCTYPE delivery [<!ENTITY l0 \"lol\">");
        for (var i = 1; i <= 9; i++)
            sb.Append($"<!ENTITY l{i} \"{string.Concat(Enumerable.Repeat($"&l{i - 1};", 10))}\">");
        sb.Append("]>");
        var sw = Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes(true);
        var ex = await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, WithDoctype(sb.ToString(), "&l9;")));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 20_000_000);
        Assert.Contains("DTD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Internal_entity_declarations_are_rejected_too()
    {
        await Assert.ThrowsAsync<SourceFormatException>(() => X.Parse(S, WithDoctype("<!DOCTYPE delivery [<!ENTITY e \"v\">]>", "&e;")));
    }

    [Fact]
    public async Task Xsi_schema_location_is_never_fetched()
    {
        var xml = X.Delivery(S, X.RecordXml(S, Samples.Row(S))).Replace("<delivery ", "<delivery xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"urn:x http://127.0.0.1:1/x.xsd\" ");
        // the hint is ignored (no schema loading flags, no resolver): the document is validated against the configured XSDs only
        Assert.True(Assert.Single(await X.Parse(S, xml)).IsAccepted);
    }
}

[CollectionDefinition("XmlScale", DisableParallelization = true)]
public class XmlScaleCollection;

[Collection("XmlScale")]
public class XmlScaleTests
{
    private const string S = SourceNames.TransferInstructions;

    /// <summary>Generates a delivery on the fly; never materialises the file.</summary>
    private sealed class GeneratedXmlStream(int records) : Stream
    {
        private readonly Encoding _enc = new UTF8Encoding(false);
        private byte[] _buf = _enc0.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<delivery source=\"{S}\" schemaVersion=\"1.0\" businessDate=\"2026-10-27\">\n");
        private static readonly Encoding _enc0 = new UTF8Encoding(false);
        private int _pos, _next;
        private bool _closed;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _buf.Length)
            {
                if (_next < records)
                {
                    var n = _next++;
                    _buf = _enc.GetBytes($"<record><source_row_id>S{n}</source_row_id><instruction_id>TRF-{n}</instruction_id><client_account_id>CA-{n % 500}</client_account_id><legal_entity_id>LE-1</legal_entity_id><transaction_type>BANK_TRANSFER</transaction_type><amount>{n % 1000}.25</amount><currency>GBP</currency><instruction_timestamp>2026-10-27T08:15:00+01:00</instruction_timestamp><source_status>SUBMITTED</source_status></record>\n");
                }
                else if (!_closed) { _buf = _enc.GetBytes("</delivery>\n"); _closed = true; }
                else return 0;
                _pos = 0;
            }
            var c = Math.Min(count, _buf.Length - _pos);
            Array.Copy(_buf, _pos, buffer, offset, c);
            _pos += c;
            return c;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task One_hundred_thousand_records_stream_in_constant_memory()
    {
        const int total = 100_000;
        var baseline = GC.GetTotalMemory(true);
        long peak = 0;
        var accepted = 0;
        decimal sum = 0;
        await foreach (var r in X.Adapter(S).ParseRecordsAsync(new GeneratedXmlStream(total), X.Descriptor(S)))
        {
            Assert.True(r.IsAccepted, r.Rejection?.Message);
            accepted++;
            sum += ((TransferInstruction)r.Record!).Amount.Amount;
            if (accepted % 5000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(true) - baseline);
        }
        Assert.Equal(total, accepted);
        Assert.Equal(100 * Enumerable.Range(0, 1000).Sum(i => i + 0.25m), sum);
        Assert.True(peak < 40_000_000, $"memory grew by {peak} bytes");
    }
}
