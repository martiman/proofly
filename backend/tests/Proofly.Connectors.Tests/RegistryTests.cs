using Proofly.Connectors.Abstractions;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests;

/// <summary>Minimal in-test adapter: yields a fixed list of results. Proves the contract is implementable without a real adapter.</summary>
public sealed class StubAdapter(string source, SourceFormat format, params ParseResult<TransferInstruction>[] rows) : ISourceAdapter<TransferInstruction>
{
    public string SourceName => source;
    public SourceFormat Format => format;
    public IReadOnlyCollection<string> SupportedSchemaVersions => ["1.0"];
    public Task<SchemaValidation> ValidateSchemaAsync(Stream content, SourceDescriptor d, CancellationToken ct = default) =>
        Task.FromResult(d.SchemaVersion == "1.0" ? SchemaValidation.Ok : SchemaValidation.Fail("unsupported"));

    public async IAsyncEnumerable<ParseResult<TransferInstruction>> ParseAsync(
        Stream content, SourceDescriptor d, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in rows) { await Task.Yield(); yield return r; }
    }
}

public class RegistryTests
{
    private static readonly SourceDescriptor Desc = new("transfer_instructions", SourceFormat.Csv, "1.0", "D1", new DateOnly(2026, 10, 27), "f.csv");

    private static TransferInstruction Rec() => new("SRC-T-001", "TRF-001", "CA-001", "LE-CZ-001", "BANK_TRANSFER", new Money(1250m, "EUR"),
        DateTimeOffset.Parse("2026-10-27T08:15:00+01:00"), new DateOnly(2026, 10, 27), TransferStatus.Submitted, "BANKREF-001", "BENEF-A");

    [Fact]
    public void Resolves_by_source_name_and_format_case_insensitively()
    {
        var csv = new StubAdapter("transfer_instructions", SourceFormat.Csv);
        var xml = new StubAdapter("transfer_instructions", SourceFormat.Xml);
        var reg = new SourceAdapterRegistry([csv, xml]);
        Assert.Same(csv, reg.Resolve("TRANSFER_INSTRUCTIONS", SourceFormat.Csv));
        Assert.Same(xml, reg.Resolve<TransferInstruction>("transfer_instructions", SourceFormat.Xml));
        Assert.Null(reg.Find("transfer_instructions", SourceFormat.Xlsx));
        Assert.Throws<KeyNotFoundException>(() => reg.Resolve("card_settlements", SourceFormat.Csv));
        Assert.Throws<InvalidCastException>(() => reg.Resolve<CardEvent>("transfer_instructions", SourceFormat.Csv));
    }

    [Fact]
    public void Duplicate_registration_is_a_configuration_error() =>
        Assert.Throws<InvalidOperationException>(() => new SourceAdapterRegistry(
            [new StubAdapter("a", SourceFormat.Csv), new StubAdapter("A", SourceFormat.Csv)]));

    [Fact]
    public async Task Widened_parse_yields_accepted_and_rejected_rows_in_order()
    {
        var rej = new RowRejection(ReasonCodes.RequiredFieldMissing, "client_account_id is empty", 2, "SRC-T-009,...", "SRC-T-009", "client_account_id", new Money(410m, "EUR"));
        ISourceAdapter a = new StubAdapter("transfer_instructions", SourceFormat.Csv,
            ParseResult<TransferInstruction>.Accept(1, Rec()), ParseResult<TransferInstruction>.Reject(rej));
        var rows = new List<ParseResult<ISourceRecord>>();
        await foreach (var r in a.ParseRecordsAsync(Stream.Null, Desc)) rows.Add(r);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsAccepted);
        Assert.Equal("TRF-001", rows[0].Record!.NaturalKey);
        Assert.Equal(new Money(1250m, "EUR"), rows[0].Record!.ControlAmount);
        Assert.Equal(ReasonCodes.RequiredFieldMissing, rows[1].Rejection!.ReasonCode);
        Assert.Equal(typeof(TransferInstruction), a.RecordType);
    }

    [Fact]
    public void Wire_enums_parse_exactly_and_reject_anything_else()
    {
        Assert.True(WireEnum.TryParse<LedgerEntryType>("DIRECT_DEBIT", out var t));
        Assert.Equal(LedgerEntryType.DirectDebit, t);
        Assert.False(WireEnum.TryParse<LedgerEntryType>("direct_debit", out _));
        Assert.False(WireEnum.TryParse<DebitCredit>(" DEBIT", out _));
        Assert.False(WireEnum.TryParse<DebitCredit>(null, out _));
    }
}
