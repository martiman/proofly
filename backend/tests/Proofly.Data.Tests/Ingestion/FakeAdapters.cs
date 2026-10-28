using System.Globalization;
using System.Runtime.CompilerServices;
using Proofly.Connectors.Abstractions;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Ingestion;

/// <summary>
/// Tiny stand-ins for the real adapters (#30-32): unquoted CSV only, same headers and rules as the synthetic fixture,
/// enough to drive the framework. They reject, never repair.
/// </summary>
public abstract class FakeCsvAdapter<T>(string source, string header) : ISourceAdapter<T> where T : class, ISourceRecord
{
    public string SourceName => source;
    public SourceFormat Format => SourceFormat.Csv;
    public IReadOnlyCollection<string> SupportedSchemaVersions => ["1.0"];

    public async Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor d, CancellationToken ct = default)
    {
        using var r = new StreamReader(content);
        var first = await r.ReadLineAsync(ct);
        return first == header ? SchemaValidation.Ok : SchemaValidation.Fail($"Header does not match {source} v1.0.");
    }

    protected abstract ParseResult<T> Build(int row, string raw, Dictionary<string, string> f);

    public async IAsyncEnumerable<ParseResult<T>> ParseAsync(Stream content, SourceDescriptor d, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var r = new StreamReader(content);
        var cols = (await r.ReadLineAsync(ct))!.Split(',');
        var n = 0;
        while (await r.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            n++;
            var parts = line.Split(',');
            var f = cols.Zip(parts, (c, v) => (c, v)).ToDictionary(x => x.c, x => x.v);
            yield return Build(n, line, f);
        }
    }

    protected static decimal? Dec(Dictionary<string, string> f, string key) =>
        decimal.TryParse(f[key], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;

    protected static Money? Amt(Dictionary<string, string> f, string amount, string currency) =>
        Dec(f, amount) is { } a && f[currency].Length == 3 && f[currency].All(c => c is >= 'A' and <= 'Z') ? new Money(a, f[currency]) : null;

    protected static ParseResult<T> Rej(int row, string raw, Dictionary<string, string> f, string code, string msg, string field, Money? amount, string? id) =>
        ParseResult<T>.Reject(new RowRejection(code, msg, row, raw, id, field, amount));
}

public sealed class FakeTransferAdapter() : FakeCsvAdapter<TransferInstruction>(SourceNames.TransferInstructions,
    "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference")
{
    protected override ParseResult<TransferInstruction> Build(int row, string raw, Dictionary<string, string> f)
    {
        var money = Amt(f, "amount", "currency");
        foreach (var req in new[] { "source_row_id", "instruction_id", "client_account_id", "legal_entity_id" })
            if (f[req].Length == 0) return Rej(row, raw, f, ReasonCodes.RequiredFieldMissing, $"{req} is empty", req, money, f["source_row_id"]);
        if (money is null) return Rej(row, raw, f, ReasonCodes.InvalidValue, "amount or currency is invalid", "amount", null, f["source_row_id"]);
        if (!WireEnum.TryParse<TransferStatus>(f["source_status"], out var st)) return Rej(row, raw, f, ReasonCodes.InvalidValue, "unknown source_status", "source_status", money, f["source_row_id"]);
        if (!DateTimeOffset.TryParse(f["instruction_timestamp"], CultureInfo.InvariantCulture, out var ts)) return Rej(row, raw, f, ReasonCodes.InvalidValue, "bad timestamp", "instruction_timestamp", money, f["source_row_id"]);
        return ParseResult<TransferInstruction>.Accept(row, new TransferInstruction(f["source_row_id"], f["instruction_id"], f["client_account_id"],
            f["legal_entity_id"], f["transaction_type"], money.Value, ts, DateOnly.Parse(f["requested_value_date"], CultureInfo.InvariantCulture), st,
            f["external_reference"].NullIfEmpty(), f["destination_reference"].NullIfEmpty()));
    }
}

public sealed class FakeBankAdapter() : FakeCsvAdapter<BankTransaction>(SourceNames.BankingTransactions,
    "external_transaction_id,external_account_id,booking_timestamp,value_date,amount,currency,debit_credit_indicator,bank_reference,transaction_status,source_timestamp")
{
    protected override ParseResult<BankTransaction> Build(int row, string raw, Dictionary<string, string> f)
    {
        var money = Amt(f, "amount", "currency");
        if (money is null) return Rej(row, raw, f, ReasonCodes.InvalidValue, "amount or currency is invalid", "amount", null, f["external_transaction_id"]);
        if (!WireEnum.TryParse<DebitCredit>(f["debit_credit_indicator"], out var dc) || !WireEnum.TryParse<BankTransactionStatus>(f["transaction_status"], out var st))
            return Rej(row, raw, f, ReasonCodes.InvalidValue, "unknown indicator or status", "debit_credit_indicator", money, f["external_transaction_id"]);
        return ParseResult<BankTransaction>.Accept(row, new BankTransaction(f["external_transaction_id"], f["external_account_id"],
            DateTimeOffset.Parse(f["booking_timestamp"], CultureInfo.InvariantCulture), DateOnly.Parse(f["value_date"], CultureInfo.InvariantCulture),
            money.Value, dc, f["bank_reference"].NullIfEmpty(), st, DateTimeOffset.Parse(f["source_timestamp"], CultureInfo.InvariantCulture)));
    }
}

internal static class StringExt
{
    public static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
