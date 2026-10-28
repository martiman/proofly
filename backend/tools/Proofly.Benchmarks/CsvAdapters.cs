using System.Globalization;
using System.Runtime.CompilerServices;
using Proofly.Connectors.Abstractions;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Benchmarks;

/// <summary>
/// Minimal streaming CSV adapters for the benchmark. Proofly.Connectors/Csv was still empty (another agent was building it) when the
/// spike started, so the benchmark carries its own: exact header check, one ParseResult per data row, required fields and
/// enum/decimal/date values validated against the documented layout, a row that fails is rejected with a reason and never repaired.
/// They are NOT the production CSV adapter and the measured ingest time includes this code, not the production parser.
/// </summary>
internal sealed class RowException(string code, string message, string? field) : Exception(message)
{
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

internal sealed class Row(string[] values, Dictionary<string, int> index)
{
    private string Raw(string col) => index.TryGetValue(col, out var i) && i < values.Length ? values[i] : throw new RowException(ReasonCodes.SchemaUnsupported, $"column {col} missing", col);
    public string S(string col) { var v = Raw(col); return v.Length == 0 ? throw new RowException(ReasonCodes.RequiredFieldMissing, $"{col} is empty", col) : v; }
    public string? O(string col) { var v = Raw(col); return v.Length == 0 ? null : v; }
    public decimal D(string col) => decimal.TryParse(S(col), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : throw new RowException(ReasonCodes.InvalidValue, $"{col} is not a decimal", col);
    public decimal? OD(string col) => O(col) is null ? null : D(col);
    public string Ccy(string col) { var v = S(col); return v.Length == 3 && v.All(c => c is >= 'A' and <= 'Z') ? v : throw new RowException(ReasonCodes.InvalidValue, $"{col} '{v}' is not an ISO 4217 code", col); }
    public Money M(string amount, string ccy) => new(D(amount), Ccy(ccy));
    public DateTimeOffset T(string col) => DateTimeOffset.TryParseExact(S(col), "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : throw new RowException(ReasonCodes.InvalidValue, $"{col} is not an ISO timestamp with offset", col);
    public DateOnly Dt(string col) => DateOnly.TryParseExact(S(col), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new RowException(ReasonCodes.InvalidValue, $"{col} is not a date", col);
    public DateOnly? ODt(string col) => O(col) is null ? null : Dt(col);
    public T E<T>(string col) where T : struct, Enum => WireEnum.TryParse<T>(S(col), out var v) ? v : throw new RowException(ReasonCodes.InvalidValue, $"{col} has an unknown value", col);
    public Money? Control(string amount, string ccy) { try { return M(amount, ccy); } catch (RowException) { return null; } }
}

internal sealed class CsvAdapter<T>(string source, string[] header, Func<Row, T> build, string idCol, string amountCol, string ccyCol) : ISourceAdapter<T> where T : class, ISourceRecord
{
    public string SourceName => source;
    public SourceFormat Format => SourceFormat.Csv;
    public IReadOnlyCollection<string> SupportedSchemaVersions { get; } = ["1.0"];
    private readonly Dictionary<string, int> _index = header.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);

    public async Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor descriptor, CancellationToken ct = default)
    {
        using var r = new StreamReader(content, leaveOpen: true);
        var line = await r.ReadLineAsync(ct);
        return line == string.Join(',', header) ? SchemaValidation.Ok : SchemaValidation.Fail($"header of {descriptor.FileName} does not match the documented columns");
    }

    public async IAsyncEnumerable<ParseResult<T>> ParseAsync(Stream content, SourceDescriptor descriptor, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var r = new StreamReader(content, leaveOpen: true, bufferSize: 1 << 16);
        await r.ReadLineAsync(ct); // header, validated by ValidateSchemaAsync
        var n = 0;
        string? line;
        while ((line = await r.ReadLineAsync(ct)) is not null)
        {
            n++;
            var row = new Row(line.Split(','), _index);
            ParseResult<T> result;
            try { result = ParseResult<T>.Accept(n, build(row)); }
            catch (RowException e)
            {
                result = ParseResult<T>.Reject(new RowRejection(e.Code, e.Message, n, null, row.O(idCol), e.Field, row.Control(amountCol, ccyCol)));
            }
            yield return result;
        }
    }
}

internal static class CsvAdapters
{
    public static IReadOnlyList<ISourceAdapter> All() =>
    [
        new CsvAdapter<TransferInstruction>(SourceNames.TransferInstructions,
            "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference".Split(','),
            r => new TransferInstruction(r.S("source_row_id"), r.S("instruction_id"), r.S("client_account_id"), r.S("legal_entity_id"), r.S("transaction_type"), r.M("amount", "currency"),
                r.T("instruction_timestamp"), r.ODt("requested_value_date"), r.E<TransferStatus>("source_status"), r.O("external_reference"), r.O("destination_reference")),
            "source_row_id", "amount", "currency"),
        new CsvAdapter<DirectDebitEvent>(SourceNames.DirectDebitEvents,
            "source_row_id,instruction_id,mandate_reference,client_account_id,legal_entity_id,amount,currency,collection_date,event_type,event_timestamp,source_status,return_reason,original_instruction_reference".Split(','),
            r => new DirectDebitEvent(r.S("source_row_id"), r.S("instruction_id"), r.S("mandate_reference"), r.S("client_account_id"), r.S("legal_entity_id"), r.M("amount", "currency"),
                r.ODt("collection_date"), r.E<DirectDebitEventType>("event_type"), r.T("event_timestamp"), r.E<DirectDebitStatus>("source_status"), r.O("return_reason"), r.O("original_instruction_reference")),
            "source_row_id", "amount", "currency"),
        new CsvAdapter<CardEvent>(SourceNames.CardTransactions,
            "source_row_id,card_event_id,card_transaction_id,client_account_id,legal_entity_id,tokenized_card_reference,event_type,event_timestamp,amount,currency,debit_credit_indicator,merchant_reference,settlement_id,original_transaction_id,source_status".Split(','),
            r => new CardEvent(r.S("source_row_id"), r.S("card_event_id"), r.S("card_transaction_id"), r.S("client_account_id"), r.S("legal_entity_id"), r.O("tokenized_card_reference"),
                r.E<CardEventType>("event_type"), r.T("event_timestamp"), r.M("amount", "currency"), r.E<DebitCredit>("debit_credit_indicator"), r.O("merchant_reference"), r.O("settlement_id"),
                r.O("original_transaction_id"), r.E<CardEventStatus>("source_status")),
            "source_row_id", "amount", "currency"),
        new CsvAdapter<CardSettlement>(SourceNames.CardSettlements,
            "source_row_id,settlement_id,processor,legal_entity_id,settlement_date,gross_amount,fees,adjustments,net_amount,currency,destination_account,source_status".Split(','),
            r =>
            {
                var c = r.Ccy("currency");
                var s = new CardSettlement(r.S("source_row_id"), r.S("settlement_id"), r.S("processor"), r.S("legal_entity_id"), r.Dt("settlement_date"), new Money(r.D("gross_amount"), c),
                    new Money(r.D("fees"), c), new Money(r.D("adjustments"), c), new Money(r.D("net_amount"), c), r.S("destination_account"), r.E<SettlementStatus>("source_status"));
                return s.NetAmount.Amount == s.GrossAmount.Amount - s.Fees.Amount + s.Adjustments.Amount ? s
                    : throw new RowException(ReasonCodes.ControlValueMismatch, "net_amount differs from gross - fees + adjustments", "net_amount");
            },
            "source_row_id", "gross_amount", "currency"),
        new CsvAdapter<LedgerEntry>(SourceNames.InternalLedgerEntries,
            "ledger_entry_id,account_id,legal_entity_id,booking_timestamp,value_date,debit_credit_indicator,amount,currency,transaction_reference,entry_type,source_status".Split(','),
            r => new LedgerEntry(r.S("ledger_entry_id"), r.S("account_id"), r.S("legal_entity_id"), r.T("booking_timestamp"), r.Dt("value_date"), r.E<DebitCredit>("debit_credit_indicator"),
                r.M("amount", "currency"), r.O("transaction_reference"), r.E<LedgerEntryType>("entry_type"), r.E<LedgerEntryStatus>("source_status")),
            "ledger_entry_id", "amount", "currency"),
        new CsvAdapter<AccountBalance>(SourceNames.ClientAccountBalances,
            "account_id,legal_entity_id,balance_date,balance_type,opening_balance_amount,balance_amount,currency,source_timestamp".Split(','),
            r =>
            {
                var c = r.Ccy("currency");
                return new AccountBalance(r.S("account_id"), r.S("legal_entity_id"), r.Dt("balance_date"), r.E<AccountBalanceType>("balance_type"),
                    r.OD("opening_balance_amount") is { } o ? new Money(o, c) : null, new Money(r.D("balance_amount"), c), r.T("source_timestamp"));
            },
            "account_id", "balance_amount", "currency"),
        new CsvAdapter<BankTransaction>(SourceNames.BankingTransactions,
            "external_transaction_id,external_account_id,booking_timestamp,value_date,amount,currency,debit_credit_indicator,bank_reference,transaction_status,source_timestamp".Split(','),
            r => new BankTransaction(r.S("external_transaction_id"), r.S("external_account_id"), r.T("booking_timestamp"), r.Dt("value_date"), r.M("amount", "currency"),
                r.E<DebitCredit>("debit_credit_indicator"), r.O("bank_reference"), r.E<BankTransactionStatus>("transaction_status"), r.T("source_timestamp")),
            "external_transaction_id", "amount", "currency"),
        new CsvAdapter<SafeguardingBalance>(SourceNames.SafeguardingBalances,
            "source_row_id,external_account_id,legal_entity_id,balance_timestamp,balance_type,balance_amount,currency,evidence_reference,source_timestamp".Split(','),
            r => new SafeguardingBalance(r.S("source_row_id"), r.S("external_account_id"), r.S("legal_entity_id"), r.T("balance_timestamp"), r.E<SafeguardingBalanceType>("balance_type"),
                r.M("balance_amount", "currency"), r.S("evidence_reference"), r.T("source_timestamp")),
            "source_row_id", "balance_amount", "currency"),
    ];
}
