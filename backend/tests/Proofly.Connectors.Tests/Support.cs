using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Csv;
using Proofly.Connectors.Schemas;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests;

/// <summary>Neutral in-test data (GBP/EUR/JPY); independent of examples/synthetic-data.</summary>
internal static class Samples
{
    public static readonly Dictionary<string, string[]> Headers = SourceSchemas.All.ToDictionary(
        s => s.SourceName, s => s.ColumnsFor("1.0").Select(c => c.Name).Concat(s.SourceName == SourceNames.DeliveryManifest ? ["declared_total_gbp", "declared_total_jpy"] : []).ToArray());

    /// <summary>One valid row per source as column -> value.</summary>
    public static Dictionary<string, string> Row(string source) => source switch
    {
        SourceNames.TransferInstructions => R("source_row_id=S1,instruction_id=TRF-1,client_account_id=CA-1,legal_entity_id=LE-1,transaction_type=BANK_TRANSFER,amount=100.50,currency=GBP,instruction_timestamp=2026-10-27T08:15:00+01:00,requested_value_date=2026-10-27,source_status=SUBMITTED,external_reference=,destination_reference=B1"),
        SourceNames.DirectDebitEvents => R("source_row_id=S1,instruction_id=DD-1,mandate_reference=M-1,client_account_id=CA-1,legal_entity_id=LE-1,amount=5000,currency=JPY,collection_date=2026-10-27,event_type=SETTLED,event_timestamp=2026-10-27T12:00:00Z,source_status=SETTLED,return_reason=,original_instruction_reference=DD-1"),
        SourceNames.InternalLedgerEntries => R("ledger_entry_id=L1,account_id=CA-1,legal_entity_id=LE-1,booking_timestamp=2026-10-27T08:16:00+01:00,value_date=2026-10-27,debit_credit_indicator=DEBIT,amount=20.25,currency=EUR,transaction_reference=TRF-1,entry_type=TRANSFER,source_status=POSTED"),
        SourceNames.ClientAccountBalances => R("account_id=CA-1,legal_entity_id=LE-1,balance_date=2026-10-27,balance_type=DAILY_ROLLFORWARD,opening_balance_amount=10000,balance_amount=-8870.125,currency=GBP,source_timestamp=2026-10-27T23:59:59+01:00"),
        SourceNames.BankingTransactions => R("external_transaction_id=B1,external_account_id=BANK-1,booking_timestamp=2026-10-27T08:25:00+01:00,value_date=2026-10-27,amount=12.5,currency=GBP,debit_credit_indicator=CREDIT,bank_reference=R1,transaction_status=BOOKED,source_timestamp=2026-10-28T05:30:00+01:00"),
        SourceNames.SafeguardingBalances => R("source_row_id=S1,external_account_id=SG-1,legal_entity_id=LE-1,balance_timestamp=2026-10-27T23:59:59+01:00,balance_type=CLOSING_AVAILABLE,balance_amount=15000,currency=EUR,evidence_reference=STMT-1,source_timestamp=2026-10-28T06:30:00+01:00"),
        SourceNames.CardTransactions => R("source_row_id=S1,card_event_id=CEV-1,card_transaction_id=CTX-1,client_account_id=CA-1,legal_entity_id=LE-1,tokenized_card_reference=TOK-1,event_type=AUTHORIZATION,event_timestamp=2026-10-27T08:05:00+01:00,amount=120,currency=EUR,debit_credit_indicator=DEBIT,merchant_reference=M1,settlement_id=,original_transaction_id=,source_status=AUTHORIZED"),
        SourceNames.CardSettlements => R("source_row_id=S1,settlement_id=CARD-1,processor=P-A,legal_entity_id=LE-1,settlement_date=2026-10-27,gross_amount=1000,fees=20,adjustments=-5,net_amount=975,currency=GBP,destination_account=BANK-1,source_status=SETTLED"),
        SourceNames.DeliveryManifest => R("source_name=transfer_instructions,delivery_id=DEL-1,business_date=2026-10-27,schema_version=1.0,file_name=transfer_instructions.csv,declared_record_count=10,expected_by=2026-10-28T06:00:00+01:00,received_at=2026-10-28T05:42:00+01:00,correction_of_delivery_id=,sequence_number=,legal_entity_id=,declared_total_gbp=121.75,declared_total_jpy=5000"),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static Dictionary<string, string> R(string s) =>
        s.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    public static IEnumerable<object[]> AllSources => Headers.Keys.Select(k => new object[] { k });

    public static string Csv(string source, params Dictionary<string, string>[] rows) =>
        string.Join("\n", new[] { string.Join(',', Headers[source]) }.Concat(rows.Select(r => string.Join(',', Headers[source].Select(h => r[h]))))) + "\n";

    public static Dictionary<string, string> With(this Dictionary<string, string> row, string column, string value) =>
        new(row) { [column] = value };

    public static SourceDescriptor Descriptor(string source, string version = "1.0") =>
        new(source, SourceFormat.Csv, version, "DEL-1", new DateOnly(2026, 10, 27), source + ".csv");

    public static ISourceAdapter Adapter(string source, CsvSourceOptions? options = null) => source switch
    {
        SourceNames.TransferInstructions => new CsvSourceAdapter<TransferInstruction>(SourceSchemas.TransferInstructions, options),
        SourceNames.DirectDebitEvents => new CsvSourceAdapter<DirectDebitEvent>(SourceSchemas.DirectDebitEvents, options),
        SourceNames.InternalLedgerEntries => new CsvSourceAdapter<LedgerEntry>(SourceSchemas.InternalLedgerEntries, options),
        SourceNames.ClientAccountBalances => new CsvSourceAdapter<AccountBalance>(SourceSchemas.ClientAccountBalances, options),
        SourceNames.BankingTransactions => new CsvSourceAdapter<BankTransaction>(SourceSchemas.BankingTransactions, options),
        SourceNames.SafeguardingBalances => new CsvSourceAdapter<SafeguardingBalance>(SourceSchemas.SafeguardingBalances, options),
        SourceNames.CardTransactions => new CsvSourceAdapter<CardEvent>(SourceSchemas.CardTransactions, options),
        SourceNames.CardSettlements => new CsvSourceAdapter<CardSettlement>(SourceSchemas.CardSettlements, options),
        SourceNames.DeliveryManifest => new CsvSourceAdapter<DeliveryManifestEntry>(SourceSchemas.DeliveryManifest, options),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static async Task<List<ParseResult<ISourceRecord>>> Parse(string source, byte[] content, CsvSourceOptions? options = null, string version = "1.0")
    {
        var list = new List<ParseResult<ISourceRecord>>();
        await foreach (var r in Adapter(source, options).ParseRecordsAsync(new MemoryStream(content), Descriptor(source, version))) list.Add(r);
        return list;
    }

    public static Task<List<ParseResult<ISourceRecord>>> Parse(string source, string text, CsvSourceOptions? options = null) =>
        Parse(source, Encoding.UTF8.GetBytes(text), options);

    public static Task<SchemaValidation> Validate(string source, string text, string version = "1.0", CsvSourceOptions? options = null) =>
        Adapter(source, options).ValidateSchemaAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Descriptor(source, version));
}
