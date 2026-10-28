using System.Security;
using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Schemas;
using Proofly.Connectors.Xml;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests.Xml;

internal static class X
{
    public static string Element(string name, string value) => value.Length == 0 ? $"<{name}/>" : $"<{name}>{SecurityElement.Escape(value)}</{name}>";

    public static string RecordXml(string source, Dictionary<string, string> row, bool pretty = false) =>
        "<record>" + string.Concat(Samples.Headers[source].Where(row.ContainsKey).Select(h => (pretty ? "\n    " : "") + Element(h, row[h]))) + (pretty ? "\n  " : "") + "</record>";

    public static string Delivery(string source, string records, string version = "1.0", string date = "2026-10-27", string root = "delivery", string? ns = null) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<{root} source=\"{source}\" schemaVersion=\"{version}\" businessDate=\"{date}\"{(ns is null ? "" : $" xmlns=\"{ns}\"")}>\n{records}\n</{root}>\n";

    public static string Xml(string source, params Dictionary<string, string>[] rows) =>
        Delivery(source, string.Join("\n", rows.Select(r => RecordXml(source, r))));

    public static SourceDescriptor Descriptor(string source, string version = "1.0") =>
        new(source, SourceFormat.Xml, version, "DEL-1", new DateOnly(2026, 10, 27), source + ".xml");

    public static ISourceAdapter Adapter(string source, XmlSourceOptions? o = null) => source switch
    {
        SourceNames.TransferInstructions => new XmlSourceAdapter<TransferInstruction>(SourceSchemas.TransferInstructions, o),
        SourceNames.DirectDebitEvents => new XmlSourceAdapter<DirectDebitEvent>(SourceSchemas.DirectDebitEvents, o),
        SourceNames.InternalLedgerEntries => new XmlSourceAdapter<LedgerEntry>(SourceSchemas.InternalLedgerEntries, o),
        SourceNames.ClientAccountBalances => new XmlSourceAdapter<AccountBalance>(SourceSchemas.ClientAccountBalances, o),
        SourceNames.BankingTransactions => new XmlSourceAdapter<BankTransaction>(SourceSchemas.BankingTransactions, o),
        SourceNames.SafeguardingBalances => new XmlSourceAdapter<SafeguardingBalance>(SourceSchemas.SafeguardingBalances, o),
        SourceNames.CardTransactions => new XmlSourceAdapter<CardEvent>(SourceSchemas.CardTransactions, o),
        SourceNames.CardSettlements => new XmlSourceAdapter<CardSettlement>(SourceSchemas.CardSettlements, o),
        SourceNames.DeliveryManifest => new XmlSourceAdapter<DeliveryManifestEntry>(SourceSchemas.DeliveryManifest, o),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static async Task<List<ParseResult<ISourceRecord>>> Parse(string source, Stream content, XmlSourceOptions? o = null)
    {
        var list = new List<ParseResult<ISourceRecord>>();
        await foreach (var r in Adapter(source, o).ParseRecordsAsync(content, Descriptor(source))) list.Add(r);
        return list;
    }

    public static Task<List<ParseResult<ISourceRecord>>> Parse(string source, string xml, XmlSourceOptions? o = null) =>
        Parse(source, new MemoryStream(Encoding.UTF8.GetBytes(xml)), o);

    public static Task<SchemaValidation> Validate(string source, string xml, string version = "1.0", XmlSourceOptions? o = null) =>
        Adapter(source, o).ValidateSchemaAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)), Descriptor(source, version));
}
