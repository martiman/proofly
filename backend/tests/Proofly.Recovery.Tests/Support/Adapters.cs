using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Recovery.Tests.Support;

/// <summary>
/// Unquoted-CSV transfer adapter (same header and rules as the synthetic fixture) that can be told to "die" after N rows:
/// <see cref="AfterRow"/> runs once per parsed row and may throw or cancel, which is how a killed worker is simulated.
/// It rejects, never repairs.
/// </summary>
public sealed class CsvTransferAdapter : ISourceAdapter<TransferInstruction>
{
    public const string Header = "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference";

    public Action<int>? AfterRow { get; set; }

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
            if (line.Length == 0) continue;
            n++;
            var f = cols.Zip(line.Split(','), (c, v) => (c, v)).ToDictionary(x => x.c, x => x.v);
            yield return Build(n, line, f);
            AfterRow?.Invoke(n);
            ct.ThrowIfCancellationRequested();
        }
    }

    private static ParseResult<TransferInstruction> Build(int row, string raw, Dictionary<string, string> f)
    {
        Money? money = decimal.TryParse(f["amount"], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)
            && f["currency"].Length == 3 ? new Money(a, f["currency"]) : null;
        ParseResult<TransferInstruction> Rej(string code, string msg, string field) =>
            ParseResult<TransferInstruction>.Reject(new RowRejection(code, msg, row, raw, f["source_row_id"], field, money));
        foreach (var req in new[] { "source_row_id", "instruction_id", "client_account_id", "legal_entity_id" })
            if (f[req].Length == 0) return Rej(ReasonCodes.RequiredFieldMissing, $"{req} is empty", req);
        if (money is null) return Rej(ReasonCodes.InvalidValue, "amount or currency is invalid", "amount");
        if (!WireEnum.TryParse<TransferStatus>(f["source_status"], out var st)) return Rej(ReasonCodes.InvalidValue, "unknown source_status", "source_status");
        return ParseResult<TransferInstruction>.Accept(row, new TransferInstruction(f["source_row_id"], f["instruction_id"], f["client_account_id"],
            f["legal_entity_id"], f["transaction_type"], money.Value, DateTimeOffset.Parse(f["instruction_timestamp"], CultureInfo.InvariantCulture),
            DateOnly.Parse(f["requested_value_date"], CultureInfo.InvariantCulture), st, null, null));
    }
}

/// <summary>Builds a transfer delivery file: <paramref name="rows"/> rows, row 5 without client account (rejected), row 9 repeats the instruction of row 1 (rejected).</summary>
public sealed record TransferFile(byte[] Bytes, int Rows, IReadOnlyList<Money> Totals)
{
    public static TransferFile Build(string date, int rows = 12)
    {
        var lines = new List<string> { CsvTransferAdapter.Header };
        var totals = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        for (var i = 1; i <= rows; i++)
        {
            var ccy = i % 3 == 0 ? "EUR" : i % 3 == 1 ? "GBP" : "JPY";
            var amount = ccy == "JPY" ? (1000 + i).ToString(CultureInfo.InvariantCulture) : (10 * i + 0.25m).ToString(CultureInfo.InvariantCulture);
            totals[ccy] = totals.GetValueOrDefault(ccy) + decimal.Parse(amount, CultureInfo.InvariantCulture);
            var client = i == 5 ? "" : "CA-001";
            var instruction = i == 9 ? "TRF-001" : $"TRF-{i:000}";
            lines.Add($"SRC-T-{i:000},{instruction},{client},LE-CZ-001,BANK_TRANSFER,{amount},{ccy},{date}T08:15:00+01:00,{date},SUBMITTED,,");
        }
        return new TransferFile(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"), rows, totals.Select(t => new Money(t.Value, t.Key)).ToList());
    }

    public DeliveryManifestEntry Manifest(string deliveryId, DateOnly date) =>
        new(SourceNames.TransferInstructions, deliveryId, date, "1.0", "transfer_instructions.csv", Rows, Totals,
            DateTimeOffset.Parse("2026-10-28T06:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-10-28T05:42:00+00:00", CultureInfo.InvariantCulture), null, null);
}
