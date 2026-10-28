using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Acceptance.Tests.Support;

/// <summary>
/// In-test CSV adapter for transfer_instructions (the shipped CSV adapters are not part of this project's scope). Same rules as the
/// documented contract: a wrong header rejects the file, a bad row is rejected with a reason code and never repaired.
/// </summary>
public sealed class TransferCsvAdapter : ISourceAdapter<TransferInstruction>
{
    public const string Header = "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference";

    public string SourceName => SourceNames.TransferInstructions;
    public SourceFormat Format => SourceFormat.Csv;
    public IReadOnlyCollection<string> SupportedSchemaVersions => ["1.0"];

    public async Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor d, CancellationToken ct = default)
    {
        using var r = new StreamReader(content);
        return await r.ReadLineAsync(ct) == Header ? SchemaValidation.Ok : SchemaValidation.Fail("Header does not match transfer_instructions v1.0.");
    }

    public async IAsyncEnumerable<ParseResult<TransferInstruction>> ParseAsync(Stream content, SourceDescriptor d, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var r = new StreamReader(content);
        var cols = (await r.ReadLineAsync(ct))!.Split(',');
        var n = 0;
        while (await r.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) { continue; }
            n++;
            var f = cols.Zip(line.Split(','), (c, v) => (c, v)).ToDictionary(x => x.c, x => x.v);
            yield return Build(n, line, f);
        }
    }

    private static ParseResult<TransferInstruction> Build(int row, string raw, Dictionary<string, string> f)
    {
        Money? money = decimal.TryParse(f["amount"], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)
            && f["currency"].Length == 3 ? new Money(a, f["currency"]) : null;
        ParseResult<TransferInstruction> Rej(string code, string msg, string field) =>
            ParseResult<TransferInstruction>.Reject(new RowRejection(code, msg, row, raw, f["source_row_id"], field, money));
        foreach (var req in new[] { "source_row_id", "instruction_id", "client_account_id", "legal_entity_id" })
        {
            if (f[req].Length == 0) { return Rej(ReasonCodes.RequiredFieldMissing, $"{req} is empty", req); }
        }
        if (money is null) { return Rej(ReasonCodes.InvalidValue, "amount or currency is invalid", "amount"); }
        if (!WireEnum.TryParse<TransferStatus>(f["source_status"], out var st)) { return Rej(ReasonCodes.InvalidValue, "unknown source_status", "source_status"); }
        return ParseResult<TransferInstruction>.Accept(row, new TransferInstruction(f["source_row_id"], f["instruction_id"], f["client_account_id"],
            f["legal_entity_id"], f["transaction_type"], money.Value, DateTimeOffset.Parse(f["instruction_timestamp"], CultureInfo.InvariantCulture),
            DateOnly.Parse(f["requested_value_date"], CultureInfo.InvariantCulture), st,
            f["external_reference"].Length == 0 ? null : f["external_reference"], f["destination_reference"].Length == 0 ? null : f["destination_reference"]));
    }
}

/// <summary>A transfer delivery file built in the test: neutral GBP/EUR/JPY rows, one row without a client account (rejected with a reason).</summary>
public sealed record TransferFile(byte[] Bytes, int Rows, IReadOnlyList<Money> Totals)
{
    /// <param name="le">Legal entity id in every row.</param>
    /// <param name="tag">Makes instruction ids unique per file.</param>
    public static TransferFile Build(string le, string date, string tag = "A", bool corrected = false)
    {
        var rows = new (string Ccy, string Amount, string Client, string Ref)[]
        {
            ("GBP", "100.00", "A1", "REF-1"),
            ("JPY", "5000", "A2", "REF-2"),
            ("EUR", "40.50", "A3", "REF-3"),
            ("GBP", "10.00", corrected ? "A4" : "", "REF-4"), // uncorrected: no client account, the adapter rejects the row with a reason, it is never repaired
        };
        var lines = new List<string> { TransferCsvAdapter.Header };
        var totals = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            totals[r.Ccy] = totals.GetValueOrDefault(r.Ccy) + decimal.Parse(r.Amount, CultureInfo.InvariantCulture);
            lines.Add($"SRC-{tag}-{i + 1},TRF-{tag}-{i + 1},{r.Client},{le},BANK_TRANSFER,{r.Amount},{r.Ccy},{date}T09:00:00+00:00,{date},SETTLED,{r.Ref},");
        }
        return new TransferFile(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"), rows.Length, totals.Select(t => new Money(t.Value, t.Key)).ToList());
    }

    public DeliveryManifestEntry Manifest(string deliveryId, DateOnly date, string? correctionOf = null) =>
        new(SourceNames.TransferInstructions, deliveryId, date, "1.0", "transfer_instructions.csv", Rows, Totals,
            DateTimeOffset.Parse("2026-10-28T06:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-10-28T05:42:00+00:00", CultureInfo.InvariantCulture), correctionOf);
}
