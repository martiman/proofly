using System.Text;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Csv;
using Proofly.Domain;
using Proofly.Domain.Records;

namespace Proofly.Connectors.Tests;

public class CsvAdapterTests
{
    [Theory, MemberData(nameof(Samples.AllSources), MemberType = typeof(Samples))]
    public async Task Valid_row_is_accepted_for_every_source(string source)
    {
        Assert.True((await Samples.Validate(source, Samples.Csv(source, Samples.Row(source)))).IsValid);
        var r = Assert.Single(await Samples.Parse(source, Samples.Csv(source, Samples.Row(source))));
        Assert.True(r.IsAccepted, r.Rejection?.Message);
        Assert.Equal(1, r.RowNumber);
    }

    [Fact]
    public async Task Transfer_fields_are_mapped_exactly()
    {
        var r = Assert.Single(await Samples.Parse(SourceNames.TransferInstructions, Samples.Csv(SourceNames.TransferInstructions, Samples.Row(SourceNames.TransferInstructions))));
        var t = Assert.IsType<TransferInstruction>(r.Record);
        Assert.Equal(new Money(100.50m, "GBP"), t.Amount);
        Assert.Equal(TransferStatus.Submitted, t.SourceStatus);
        Assert.Equal(TimeSpan.FromHours(1), t.InstructionTimestamp.Offset);
        Assert.Null(t.ExternalReference); // blank optional cell = absent
        Assert.Equal("B1", t.DestinationReference);
    }

    [Fact]
    public async Task Required_blank_is_REQUIRED_FIELD_MISSING_with_field_and_control_amount()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("client_account_id", ""))));
        var rej = r.Rejection!;
        Assert.Equal(ReasonCodes.RequiredFieldMissing, rej.ReasonCode);
        Assert.Equal("client_account_id", rej.Field);
        Assert.Equal("client_account_id is empty", rej.Message);
        Assert.Equal("S1", rej.SourceRowId);
        Assert.Equal(new Money(100.50m, "GBP"), rej.ControlAmount);
        Assert.Contains("TRF-1", rej.RawExcerpt);
    }

    [Theory]
    [InlineData("amount", "1.1234567", "more than 6 fraction digits")]
    [InlineData("amount", "1,5", "not a decimal")]
    [InlineData("amount", "1e3", "not a decimal")]
    [InlineData("amount", "+5", "not a decimal")]
    [InlineData("amount", " 5", "whitespace")]
    [InlineData("amount", "-5", "negative")]
    [InlineData("currency", "EURO", "ISO 4217")]
    [InlineData("currency", "eur", "ISO 4217")]
    [InlineData("currency", "XXX", "ISO 4217")]
    [InlineData("instruction_timestamp", "2026-10-27T08:15:00", "explicit offset")]
    [InlineData("instruction_timestamp", "2026-10-27 08:15:00+01:00", "explicit offset")]
    [InlineData("instruction_timestamp", "2026-13-27T08:15:00+01:00", "valid ISO 8601")]
    [InlineData("requested_value_date", "27.10.2026", "yyyy-MM-dd")]
    [InlineData("requested_value_date", "2026-02-30", "yyyy-MM-dd")]
    [InlineData("source_status", "submitted", "not one of")]
    [InlineData("source_status", "WEIRD", "not one of")]
    [InlineData("instruction_id", "TRF-1 ", null)]
    public async Task Invalid_values_are_rejected_never_repaired(string column, string value, string? messagePart)
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With(column, value.Contains(',') ? "\"" + value + "\"" : value))));
        if (column == "instruction_id") { Assert.True(r.IsAccepted); Assert.Equal("TRF-1 ", ((TransferInstruction)r.Record!).InstructionId); return; } // strings are verbatim
        var rej = Assert.IsType<RowRejection>(r.Rejection);
        Assert.Equal(ReasonCodes.InvalidValue, rej.ReasonCode);
        Assert.Equal(column, rej.Field);
        Assert.Contains(messagePart!, rej.Message);
    }

    [Fact]
    public async Task Invalid_currency_leaves_control_amount_null_but_invalid_other_field_keeps_it()
    {
        var s = SourceNames.TransferInstructions;
        var bad = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("currency", "EURO"))));
        Assert.Null(bad.Rejection!.ControlAmount);
        var ok = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("source_status", "NOPE"))));
        Assert.Equal(new Money(100.50m, "GBP"), ok.Rejection!.ControlAmount);
    }

    [Fact]
    public async Task Six_fraction_digits_and_zero_decimal_currency_are_accepted()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("amount", "0.123456"))));
        Assert.Equal(0.123456m, ((TransferInstruction)r.Record!).Amount.Amount);
        var jpy = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("amount", "5000").With("currency", "JPY"))));
        Assert.Equal(new Money(5000m, "JPY"), ((TransferInstruction)jpy.Record!).Amount);
    }

    [Fact]
    public async Task Negative_safeguarding_balance_is_INVALID_VALUE_but_negative_client_balance_is_accepted()
    {
        var s = SourceNames.SafeguardingBalances;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("balance_amount", "-10"))));
        Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode);
        Assert.Equal(new Money(-10m, "EUR"), r.Rejection.ControlAmount);
        var c = SourceNames.ClientAccountBalances;
        var ok = Assert.Single(await Samples.Parse(c, Samples.Csv(c, Samples.Row(c))));
        Assert.Equal(-8870.125m, ((AccountBalance)ok.Record!).Balance.Amount);
    }

    [Fact]
    public async Task Settlement_net_mismatch_is_CONTROL_VALUE_MISMATCH_and_blank_destination_is_required()
    {
        var s = SourceNames.CardSettlements;
        var mismatch = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("net_amount", "970"))));
        Assert.Equal(ReasonCodes.ControlValueMismatch, mismatch.Rejection!.ReasonCode);
        Assert.Equal(new Money(1000m, "GBP"), mismatch.Rejection.ControlAmount);
        Assert.Contains("975", mismatch.Rejection.Message);
        var same = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("net_amount", "975.000"))));
        Assert.True(same.IsAccepted);
        var dest = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("destination_account", ""))));
        Assert.Equal(ReasonCodes.RequiredFieldMissing, dest.Rejection!.ReasonCode);
    }

    [Fact]
    public async Task Missing_required_beats_invalid_value_in_column_order()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("currency", "EURO").With("client_account_id", ""))));
        Assert.Equal(ReasonCodes.RequiredFieldMissing, r.Rejection!.ReasonCode);
    }

    [Fact]
    public async Task Row_ids_use_the_natural_id_for_sources_without_source_row_id()
    {
        var s = SourceNames.InternalLedgerEntries;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("currency", "EURO"))));
        Assert.Equal("L1", r.Rejection!.SourceRowId);
    }

    [Fact]
    public async Task Manifest_reads_generic_declared_totals_and_rejects_bad_ones()
    {
        var s = SourceNames.DeliveryManifest;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s))));
        var m = Assert.IsType<DeliveryManifestEntry>(r.Record);
        Assert.Equal(10, m.DeclaredRecordCount);
        Assert.Equal([new Money(121.75m, "GBP"), new Money(5000m, "JPY")], m.DeclaredTotals.OrderBy(t => t.Currency));
        Assert.Null(m.CorrectionOfDeliveryId);
        var bad = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("declared_total_gbp", "1.5.5"))));
        Assert.Equal(ReasonCodes.InvalidValue, bad.Rejection!.ReasonCode);
        Assert.Equal("declared_total_gbp", bad.Rejection.Field);
        var hdr = (await Samples.Validate(s, "source_name,delivery_id,business_date,schema_version,file_name,declared_total_euro\n"));
        Assert.False(hdr.IsValid);
    }

    // ---- header validation

    [Fact]
    public async Task Header_problems_fail_the_schema_check()
    {
        var s = SourceNames.BankingTransactions;
        var cols = Samples.Headers[s];
        Assert.False((await Samples.Validate(s, string.Join(',', cols.Skip(1)) + "\n")).IsValid);                        // missing
        var missing = await Samples.Validate(s, string.Join(',', cols.Skip(1)) + "\n");
        Assert.Contains("external_transaction_id", missing.Message);
        Assert.Contains("unknown", (await Samples.Validate(s, string.Join(',', cols) + ",extra\n")).Message);              // unknown
        Assert.Contains("duplicate", (await Samples.Validate(s, string.Join(',', cols) + ",amount\n")).Message);          // duplicate
        Assert.False((await Samples.Validate(s, string.Join(',', cols.Select(c => c.ToUpperInvariant())) + "\n")).IsValid); // case-sensitive
        Assert.False((await Samples.Validate(s, "")).IsValid);                                                            // no header
        var v = await Samples.Validate(s, string.Join(',', cols) + "\n", version: "9.9");
        Assert.False(v.IsValid);
        Assert.Equal(ReasonCodes.SchemaUnsupported, v.ReasonCode);
    }

    [Fact]
    public async Task Optional_columns_may_be_absent_and_are_treated_as_blank()
    {
        var s = SourceNames.BankingTransactions;
        var row = Samples.Row(s);
        var cols = Samples.Headers[s].Where(c => c != "bank_reference").ToArray();
        var csv = string.Join(',', cols) + "\n" + string.Join(',', cols.Select(c => row[c])) + "\n";
        Assert.True((await Samples.Validate(s, csv)).IsValid);
        var r = Assert.Single(await Samples.Parse(s, csv));
        Assert.Null(((BankTransaction)r.Record!).BankReference);
    }

    // ---- CSV dialect

    private static string BankCsv(Dictionary<string, string>? row = null) => Samples.Csv(SourceNames.BankingTransactions, row ?? Samples.Row(SourceNames.BankingTransactions));

    [Fact]
    public async Task Bom_and_crlf_are_handled()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BankCsv().Replace("\n", "\r\n") + "\r\n")).ToArray();
        var r = Assert.Single(await Samples.Parse(SourceNames.BankingTransactions, bytes));
        Assert.True(r.IsAccepted);
        Assert.True((await Samples.Adapter(SourceNames.BankingTransactions).ValidateSchemaAsync(new MemoryStream(bytes), Samples.Descriptor(SourceNames.BankingTransactions))).IsValid);
    }

    [Fact]
    public async Task Quoted_fields_may_contain_delimiters_quotes_and_newlines()
    {
        var s = SourceNames.TransferInstructions;
        var r = Assert.Single(await Samples.Parse(s, Samples.Csv(s, Samples.Row(s).With("destination_reference", "\"A, \"\"B\"\"\nline2\""))));
        Assert.Equal("A, \"B\"\nline2", ((TransferInstruction)r.Record!).DestinationReference);
    }

    [Fact]
    public async Task Semicolon_delimiter_is_configurable()
    {
        var csv = BankCsv().Replace(',', ';');
        var opts = new CsvSourceOptions { Delimiter = ';' };
        Assert.True((await Samples.Validate(SourceNames.BankingTransactions, csv, options: opts)).IsValid);
        Assert.True(Assert.Single(await Samples.Parse(SourceNames.BankingTransactions, csv, opts)).IsAccepted);
        Assert.False((await Samples.Validate(SourceNames.BankingTransactions, csv)).IsValid); // default comma sees one unknown column
        Assert.Throws<InvalidOperationException>(() => new CsvSourceOptions { Delimiter = '\t' }.Validate());
    }

    [Fact]
    public async Task Windows_1250_and_iso_8859_2_are_decoded_when_declared()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var s = SourceNames.TransferInstructions;
        var csv = Samples.Csv(s, Samples.Row(s).With("destination_reference", "Žluťoučký kůň"));
        foreach (var name in new[] { "windows-1250", "iso-8859-2" })
        {
            var bytes = Encoding.GetEncoding(name).GetBytes(csv);
            var r = Assert.Single(await Samples.Parse(s, bytes, new CsvSourceOptions { Encoding = name }));
            Assert.Equal("Žluťoučký kůň", ((TransferInstruction)r.Record!).DestinationReference);
        }
        Assert.Throws<InvalidOperationException>(() => new CsvSourceOptions { Encoding = "utf-16" }.Validate());
    }

    [Fact]
    public async Task Undecodable_bytes_make_the_file_unreadable()
    {
        var bytes = Encoding.UTF8.GetBytes(BankCsv()).Concat(new byte[] { 0xFF, 0xFE, 0xFD, 0x0A }).ToArray();
        await Assert.ThrowsAsync<SourceFormatException>(() => Samples.Parse(SourceNames.BankingTransactions, bytes));
    }

    [Fact]
    public async Task Unterminated_quote_makes_the_file_unreadable()
    {
        await Assert.ThrowsAsync<SourceFormatException>(() =>
            Samples.Parse(SourceNames.BankingTransactions, BankCsv() + "B2,\"oops,2026\n"));
    }

    [Fact]
    public async Task Blank_lines_are_skipped_and_not_counted()
    {
        var b = SourceNames.BankingTransactions;
        var good = Samples.Row(b);
        var line = string.Join(',', Samples.Headers[b].Select(h => good[h]));
        var csv = string.Join("\n", string.Join(',', Samples.Headers[b]), "", line, "", "", line.Replace("B1,", "B2,"), "");
        var rs = await Samples.Parse(b, csv);
        Assert.Equal([1, 2], rs.Select(r => r.RowNumber));
        Assert.All(rs, r => Assert.True(r.IsAccepted));
    }

    [Fact]
    public async Task Ragged_and_malformed_rows_are_rejected_not_fatal_and_following_rows_still_parse()
    {
        var b = SourceNames.BankingTransactions;
        var good = Samples.Row(b);
        var line = string.Join(',', Samples.Headers[b].Select(h => good[h]));
        var csv = string.Join("\n", string.Join(',', Samples.Headers[b]),
            line.Replace("B1,", "B2,") + ",extra",       // too many
            "B3,BANK-1,short",                             // too few
            line.Replace("B1,", "B4,").Replace("BANK-1", "BA\"NK-1"), // stray quote
            line.Replace("B1,", "B5,").Replace("BANK-1", "\"BANK-1\"x"), // text after closing quote
            line) + "\n";
        var rs = await Samples.Parse(b, csv);
        Assert.Equal(5, rs.Count);
        Assert.All(rs.Take(4), r => Assert.Equal(ReasonCodes.InvalidValue, r.Rejection!.ReasonCode));
        Assert.Equal("B2", rs[0].Rejection!.SourceRowId);
        Assert.Contains("cells", rs[0].Rejection!.Message);
        Assert.Contains("malformed", rs[2].Rejection!.Message);
        Assert.True(rs[4].IsAccepted);
        Assert.Equal(5, rs[4].RowNumber);
    }

    [Fact]
    public async Task Header_failure_on_parse_is_a_file_level_error()
    {
        await Assert.ThrowsAsync<SourceFormatException>(() => Samples.Parse(SourceNames.BankingTransactions, "a,b\n1,2\n"));
    }

    [Fact]
    public async Task Large_files_stream()
    {
        var b = SourceNames.BankingTransactions;
        var good = Samples.Row(b);
        var sb = new StringBuilder(string.Join(',', Samples.Headers[b]) + "\n");
        for (var i = 0; i < 20000; i++) sb.Append(string.Join(',', Samples.Headers[b].Select(h => h == "external_transaction_id" ? "B" + i : good[h]))).Append('\n');
        var rs = await Samples.Parse(b, sb.ToString());
        Assert.Equal(20000, rs.Count);
        Assert.All(rs, r => Assert.True(r.IsAccepted));
    }

    // ---- committed sample files (no currency or count assertions: the dataset is being changed)

    [Theory]
    [InlineData("banking_transactions")] [InlineData("card_settlements")] [InlineData("card_transactions")] [InlineData("client_account_balances")]
    [InlineData("delivery_manifest")] [InlineData("direct_debit_events")] [InlineData("internal_ledger_entries")]
    [InlineData("safeguarding_balances")] [InlineData("transfer_instructions")]
    public async Task Committed_sample_files_parse_without_file_level_errors(string source)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "examples", "synthetic-data", "2026-10-27"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir!, "examples", "synthetic-data", "2026-10-27", source + ".csv"));
        var adapter = Samples.Adapter(source);
        var v = await adapter.ValidateSchemaAsync(new MemoryStream(bytes), Samples.Descriptor(source));
        Assert.True(v.IsValid, v.Message);
        var rs = await Samples.Parse(source, bytes);
        Assert.NotEmpty(rs);
        Assert.All(rs, r => Assert.True(r.IsAccepted ^ (r.Rejection is { ReasonCode.Length: > 0, Message.Length: > 0 })));
    }

    /// <summary>Adapter-level findings of expected_findings.csv (row-level reason codes only) are reproduced exactly: nothing more, nothing less.</summary>
    [Fact]
    public async Task Sample_files_reproduce_the_row_level_expected_findings()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "examples", "synthetic-data", "2026-10-27"))) dir = Path.GetDirectoryName(dir);
        var root = Path.Combine(dir!, "examples", "synthetic-data", "2026-10-27");
        string[] adapterCodes = [ReasonCodes.RequiredFieldMissing, ReasonCodes.InvalidValue, ReasonCodes.ControlValueMismatch];
        var expected = (await File.ReadAllLinesAsync(Path.Combine(root, "expected_findings.csv"))).Skip(1)
            .Select(l => l.Split(',')).Where(c => c[1] != "delivery_manifest.csv" && adapterCodes.Contains(c[4]))
            .GroupBy(c => c[1]).ToDictionary(g => g.Key, g => g.ToDictionary(c => c[2], c => c[4]));
        foreach (var source in SourceNames.DataSources)
        {
            var rs = await Samples.Parse(source, await File.ReadAllBytesAsync(Path.Combine(root, source + ".csv")));
            var actual = rs.Where(r => !r.IsAccepted).ToDictionary(r => r.Rejection!.SourceRowId!, r => r.Rejection!.ReasonCode);
            Assert.Equal(expected.GetValueOrDefault(source + ".csv") ?? [], actual);
        }
    }
}
