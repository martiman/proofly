using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Proofly.Connectors.Abstractions;
using Proofly.Data.Audit;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Tests.Ingestion;

/// <summary>Fails the audit append once armed: proves registration/ingestion roll back together with their audit event.</summary>
internal sealed class FailingAudit(IAuditWriter inner) : IAuditWriter
{
    public bool Armed;
    public Task<AuditEventRecord> AppendAsync(NpgsqlTransaction tx, AuditEventInput input, CancellationToken ct = default) =>
        Armed ? throw new InvalidOperationException("audit down") : inner.AppendAsync(tx, input, ct);
}

/// <summary>One schema per test (audit_events is append-only and cannot be truncated), so counts are exact.</summary>
public sealed class IngestionTests : IAsyncLifetime
{
    private readonly SchemaFixture _f = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proofly-ing-" + Guid.NewGuid().ToString("N"));
    private EvidenceStore _evidence = null!;
    private FailingAudit _audit = null!;
    private DeliveryRegistry _registry = null!;
    private DeliveryIngestor _ingestor = null!;
    private IngestionOptions _options = null!;

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await new MigrationRunner(_f.DataSource).RunAsync();
        await _f.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-CZ-001','Test','EUR') ON CONFLICT DO NOTHING");
        _evidence = new EvidenceStore(new EvidenceOptions { Directory = _dir });
        _audit = new FailingAudit(new AuditWriter());
        _options = new IngestionOptions();
        var adapters = new SourceAdapterRegistry([new FakeTransferAdapter(), new FakeBankAdapter()]);
        _registry = new DeliveryRegistry(_f.DataSource, _audit, new JobStore(_f.DataSource), adapters, _options);
        _ingestor = new DeliveryIngestor(_f.DataSource, _audit, _evidence, adapters, _options);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_dir))
        {
            foreach (var x in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(x, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        await _f.DisposeAsync();
    }

    // Neutral in-test example: three currencies, one without decimals. Independent of examples/synthetic-data.
    private const string TrfHeader = "source_row_id,instruction_id,client_account_id,legal_entity_id,transaction_type,amount,currency,instruction_timestamp,requested_value_date,source_status,external_reference,destination_reference";
    private const string BankHeader = "external_transaction_id,external_account_id,booking_timestamp,value_date,amount,currency,debit_credit_indicator,bank_reference,transaction_status,source_timestamp";
    private static readonly DateOnly Day = new(2026, 10, 27);

    private static string TrfRow(string n, string amount, string ccy, string client = "CA-001", string instruction = "") =>
        $"SRC-T-{n},{(instruction.Length > 0 ? instruction : "TRF-" + n)},{client},LE-CZ-001,BANK_TRANSFER,{amount},{ccy},2026-10-27T08:15:00+01:00,2026-10-27,SUBMITTED,,";

    /// <summary>6 rows: GBP 100.50 + 20.25 + 1 (dup), EUR 300 + 10 (missing client), JPY 5000. 4 accepted, 2 rejected.</summary>
    private static byte[] TransferFile() => Encoding.UTF8.GetBytes(string.Join("\n", TrfHeader,
        TrfRow("001", "100.50", "GBP"), TrfRow("002", "20.25", "GBP"), TrfRow("003", "300", "EUR"), TrfRow("004", "5000", "JPY"),
        TrfRow("005", "10", "EUR", client: ""), TrfRow("006", "1", "GBP", instruction: "TRF-001")) + "\n");

    private static byte[] BankFile() => Encoding.UTF8.GetBytes(string.Join("\n", BankHeader,
        "BTX-001,BANK-GBP,2026-10-27T08:25:00+01:00,2026-10-27,12.5,GBP,DEBIT,R1,BOOKED,2026-10-28T05:30:00+01:00",
        "BTX-002,BANK-GBP,2026-10-27T08:26:00+01:00,2026-10-27,7,GBP,DEBIT,R2,BOOKED,2026-10-28T05:30:00+01:00",
        "BTX-003,BANK-EUR,2026-10-27T08:27:00+01:00,2026-10-27,3,EUR,CREDIT,R3,BOOKED,2026-10-28T05:30:00+01:00") + "\n");

    private static readonly Money[] TransferTotals = [new(121.75m, "GBP"), new(310m, "EUR"), new(5000m, "JPY")];
    private static readonly Money[] BankTotals = [new(19.5m, "GBP"), new(3m, "EUR")];
    private static readonly Dictionary<string, byte[]> Files = new()
    {
        [SourceNames.TransferInstructions] = TransferFile(), [SourceNames.BankingTransactions] = BankFile(),
    };

    private static DeliveryManifestEntry Manifest(string source, string? id = null, int? count = null, IReadOnlyList<Money>? totals = null,
        string? correctionOf = null, string? schema = null, int? seq = null)
    {
        var trf = source == SourceNames.TransferInstructions;
        return new DeliveryManifestEntry(source, id ?? (trf ? "DEL-TRF-01" : "DEL-BANK-01"), Day, schema ?? "1.0", source + ".csv",
            count ?? (trf ? 6 : 3), totals ?? (trf ? TransferTotals : BankTotals),
            DateTimeOffset.Parse("2026-10-28T06:00:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-10-28T05:42:00+00:00", CultureInfo.InvariantCulture), correctionOf, seq);
    }

    private async Task<RegistrationResult> Register(DeliveryManifestEntry m, byte[]? content = null, SourceFormat format = SourceFormat.Csv)
    {
        content ??= Files[m.SourceName];
        var obj = await _evidence.WriteAsync(new MemoryStream(content));
        return await _registry.RegisterAsync(new DeliveryRegistration(m, obj.Sha256, format, "LE-CZ-001"));
    }

    private Task<long> Count(string sql) => _f.Scalar<long>(sql)!;
    private Task<string?> StateOf(string id) => _f.Scalar<string>($"SELECT state FROM source_deliveries WHERE id='{id}'");
    private Task<long> Audits(string type, string subject) => Count($"SELECT count(*) FROM audit_events WHERE event_type='{type}' AND subject_id='{subject}'");

    [Fact]
    public async Task Transfer_delivery_qualifies_rejected_rows_with_reason_and_location()
    {
        var m = Manifest(SourceNames.TransferInstructions);
        var reg = await Register(m);
        Assert.Equal(DeliveryState.Received, reg.State);
        Assert.Equal(1, await Count("SELECT count(*) FROM jobs WHERE type='delivery.ingest'"));

        var o = await _ingestor.IngestAsync(m.DeliveryId);

        Assert.Equal(DeliveryState.Qualified, o.State);
        Assert.Equal(4, o.AcceptedCount);
        Assert.Equal(2, o.RejectedCount);
        // visible reason and row location for each rejected row
        Assert.Equal(ReasonCodes.RequiredFieldMissing, await _f.Scalar<string>("SELECT reason_code FROM delivery_rejections WHERE source_row_id='SRC-T-005'"));
        Assert.Equal(ReasonCodes.DuplicateRecord, await _f.Scalar<string>("SELECT reason_code FROM delivery_rejections WHERE source_row_id='SRC-T-006'"));
        Assert.Equal(5, await _f.Scalar<int>("SELECT row_number FROM delivery_rejections WHERE source_row_id='SRC-T-005'"));
        Assert.Contains("SRC-T-005", (await _f.Scalar<string>("SELECT raw_excerpt FROM delivery_rejections WHERE source_row_id='SRC-T-005'"))!);
        Assert.Equal(4, await Count("SELECT count(*) FROM transfer_instructions"));
        // denominator: 6 rows seen, 6 declared; rejected rows count in the per-currency totals (all declared totals match)
        var snap = (await _registry.GetAsync(m.DeliveryId))!;
        Assert.Equal(6, snap.TotalRowCount);
        Assert.Equal(4, snap.AcceptedRecordCount);
        Assert.Equal(2, snap.RejectedRecordCount);
        Assert.NotNull(snap.IngestedAt);
        foreach (var t in TransferTotals)
        {
            Assert.Equal(t.Amount, await _f.Scalar<decimal>($"SELECT declared_amount FROM delivery_control_totals WHERE currency='{t.Currency}'"));
            Assert.Equal(t.Amount, await _f.Scalar<decimal>($"SELECT actual_amount FROM delivery_control_totals WHERE currency='{t.Currency}'"));
        }
        Assert.Equal(3, await Count("SELECT count(*) FROM delivery_control_totals"));
        Assert.Empty(o.Failures);
        Assert.Equal(1, await Audits("DELIVERY_RECEIVED", m.DeliveryId) - 1); // registration + ingestion events
        Assert.Equal(2, await Count($"SELECT count(*) FROM audit_events WHERE subject_id='{m.DeliveryId}' AND event_type='RECORD_REJECTED'"));
        Assert.True((await new AuditVerifier(_f.DataSource).VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Banking_count_mismatch_rejects_the_delivery_and_stores_no_records()   // F-014
    {
        var m = Manifest(SourceNames.BankingTransactions, count: 2);
        await Register(m);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(DeliveryState.Rejected, o.State);
        Assert.Contains(o.Failures, f => f.ReasonCode == ReasonCodes.ControlCountMismatch && f.Message.Contains("2") && f.Message.Contains("3"));
        Assert.Equal(0, await Count("SELECT count(*) FROM bank_transactions"));
        Assert.Equal(1, await Count($"SELECT count(*) FROM delivery_rejections WHERE delivery_id='{m.DeliveryId}' AND reason_code='CONTROL_COUNT_MISMATCH' AND source_row_id IS NULL"));
    }

    [Fact]
    public async Task Count_mismatch_can_be_qualified_by_source_policy()
    {
        _options.Sources[SourceNames.BankingTransactions] = new SourcePolicy { CountMismatch = MismatchOutcome.Qualify };
        var m = Manifest(SourceNames.BankingTransactions, count: 2);
        await Register(m);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(DeliveryState.Qualified, o.State);
        Assert.Equal(3, await Count("SELECT count(*) FROM bank_transactions"));
    }

    [Fact]
    public async Task Value_total_mismatch_qualifies_with_decimal_comparison()
    {
        var m = Manifest(SourceNames.TransferInstructions, totals: [new(121.74m, "GBP"), new(310m, "EUR"), new(5000m, "JPY")]);
        await Register(m);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(DeliveryState.Qualified, o.State);
        var f = Assert.Single(o.Failures);
        Assert.Equal(ReasonCodes.ControlValueMismatch, f.ReasonCode);
        Assert.Contains("121.74 GBP", f.Message);
        Assert.Contains("121.75 GBP", f.Message);
        Assert.Equal(4, await Count("SELECT count(*) FROM transfer_instructions"));
    }

    [Fact]
    public async Task Total_is_checked_for_every_currency_in_declared_or_actual()
    {
        // JPY declared but absent from the file, EUR measured but not declared, GBP matches
        var file = Encoding.UTF8.GetBytes(TrfHeader + "\n" + TrfRow("001", "5", "GBP") + "\n" + TrfRow("002", "7", "EUR") + "\n");
        var m = Manifest(SourceNames.TransferInstructions, count: 2, totals: [new(5m, "GBP"), new(100m, "JPY")]);
        await Register(m, file);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(DeliveryState.Qualified, o.State);
        Assert.Equal(2, o.Failures.Count);
        Assert.Contains(o.Failures, f => f.ReasonCode == ReasonCodes.ControlValueMismatch && f.Message.Contains("100.00 JPY") && f.Message.Contains("0.00 JPY"));
        Assert.Contains(o.Failures, f => f.ReasonCode == ReasonCodes.ControlValueMismatch && f.Message.Contains("no EUR total") && f.Message.Contains("7.00 EUR"));
        Assert.Equal(100m, await _f.Scalar<decimal>("SELECT declared_amount FROM delivery_control_totals WHERE currency='JPY'"));
        Assert.Equal(7m, await _f.Scalar<decimal>("SELECT actual_amount FROM delivery_control_totals WHERE currency='EUR'"));
        Assert.Null(await _f.Scalar<decimal?>("SELECT declared_amount FROM delivery_control_totals WHERE currency='EUR'"));
    }

    [Fact]
    public async Task Manifest_without_any_declared_total_skips_the_value_check()
    {
        var m = Manifest(SourceNames.TransferInstructions, totals: []);
        await Register(m);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.DoesNotContain(o.Failures, f => f.ReasonCode == ReasonCodes.ControlValueMismatch);
        Assert.Equal(3, await Count("SELECT count(*) FROM delivery_control_totals WHERE declared_amount IS NULL AND actual_amount IS NOT NULL"));
    }

    [Fact]
    public async Task Value_mismatch_rejects_when_source_policy_says_so()
    {
        _options.Sources[SourceNames.TransferInstructions] = new SourcePolicy { ValueMismatch = MismatchOutcome.Reject };
        var m = Manifest(SourceNames.TransferInstructions, totals: [new(1m, "GBP"), new(310m, "EUR"), new(5000m, "JPY")]);
        await Register(m);
        Assert.Equal(DeliveryState.Rejected, (await _ingestor.IngestAsync(m.DeliveryId)).State);
    }

    [Fact]
    public async Task Clean_delivery_ends_received_and_ingestion_is_idempotent()
    {
        var content = OneRow;
        var m = Manifest(SourceNames.TransferInstructions, count: 1, totals: [new(1250m, "JPY")]);
        var r1 = await Register(m, content);
        var r2 = await Register(m, content);
        Assert.True(r1.Created);
        Assert.False(r2.Created);
        Assert.Equal(1, await Count("SELECT count(*) FROM jobs"));
        Assert.Equal(1, await Count("SELECT count(*) FROM audit_events WHERE event_type='DELIVERY_RECEIVED'"));

        var job = new IngestDeliveryJob(_ingestor);
        var ctx = new JobContext(1, 1, 5, null, m.DeliveryId);
        await job.HandleAsync(new IngestDeliveryPayload(m.DeliveryId), ctx, default);
        await job.HandleAsync(new IngestDeliveryPayload(m.DeliveryId), ctx, default);

        Assert.Equal("RECEIVED", await StateOf(m.DeliveryId));
        Assert.Equal(1, await Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(1, await Count("SELECT count(*) FROM source_deliveries WHERE ingested_at IS NOT NULL"));
        Assert.Equal(2, await Count("SELECT count(*) FROM audit_events WHERE event_type='DELIVERY_RECEIVED'"));
        Assert.True((await _ingestor.IngestAsync(m.DeliveryId)).AlreadyIngested);
    }

    private static readonly byte[] OneRow = Encoding.UTF8.GetBytes(TrfHeader + "\n" + TrfRow("001", "1250", "JPY") + "\n");

    [Fact]
    public async Task Same_bytes_under_a_new_delivery_id_is_a_duplicate_delivery()
    {
        var m = Manifest(SourceNames.TransferInstructions);
        await Register(m);
        await _ingestor.IngestAsync(m.DeliveryId);
        var dup = await Register(m with { DeliveryId = "DEL-TRF-DUP" });
        Assert.Equal(DeliveryState.Rejected, dup.State);
        Assert.Contains(dup.Failures, f => f.ReasonCode == ReasonCodes.DuplicateDelivery && f.Message.Contains(m.DeliveryId));
        Assert.Equal(1, await Count("SELECT count(*) FROM jobs"));
        Assert.Equal(4, await Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal("QUALIFIED", await StateOf(m.DeliveryId));
        Assert.Equal(1, await Audits("DELIVERY_RECEIVED", "DEL-TRF-DUP"));
    }

    [Fact]
    public async Task Same_id_with_different_content_is_refused_and_audited()
    {
        var m = Manifest(SourceNames.TransferInstructions);
        await Register(m);
        var r = await Register(m, Encoding.UTF8.GetBytes(TrfHeader + "\n"));
        Assert.True(r.IdConflict);
        Assert.Equal(1, await Count("SELECT count(*) FROM source_deliveries"));
        Assert.Equal(2, await Audits("DELIVERY_RECEIVED", m.DeliveryId));
    }

    [Fact]
    public async Task Second_delivery_needs_a_correction_link_and_then_supersedes_the_original()
    {
        var m = Manifest(SourceNames.TransferInstructions);
        await Register(m);
        await _ingestor.IngestAsync(m.DeliveryId);
        var corrected = OneRow;
        var corr = Manifest(SourceNames.TransferInstructions, id: "DEL-TRF-20261027-02", count: 1, totals: [new(1250m, "JPY")]);

        var unlinked = await Register(corr, corrected);
        Assert.Equal(DeliveryState.Rejected, unlinked.State);
        Assert.Contains(unlinked.Failures, f => f.ReasonCode == ReasonCodes.CorrectionLinkMissing);

        var linked = await Register(corr with { DeliveryId = "DEL-TRF-20261027-03", CorrectionOfDeliveryId = m.DeliveryId }, corrected);
        Assert.Equal(DeliveryState.Received, linked.State);
        Assert.Equal("QUALIFIED", await StateOf(m.DeliveryId));
        var o = await _ingestor.IngestAsync("DEL-TRF-20261027-03");
        Assert.Equal(DeliveryState.Received, o.State);
        Assert.Equal("SUPERSEDED", await StateOf(m.DeliveryId));
        Assert.Equal(m.DeliveryId, (await _registry.GetAsync("DEL-TRF-20261027-03"))!.SupersedesDeliveryId);
        Assert.Equal(1, await Audits("DELIVERY_SUPERSEDED", m.DeliveryId));
        // the original's records are kept (immutable); a correction never overwrites
        Assert.Equal(4, await Count($"SELECT count(*) FROM transfer_instructions WHERE delivery_id='{m.DeliveryId}'"));
        Assert.Equal(1, await Count("SELECT count(*) FROM transfer_instructions WHERE delivery_id='DEL-TRF-20261027-03'"));
    }

    [Fact]
    public async Task Unsupported_schema_version_is_rejected_at_registration_without_a_job()
    {
        var r = await Register(Manifest(SourceNames.TransferInstructions, schema: "9.9"));
        Assert.Equal(DeliveryState.Rejected, r.State);
        Assert.Contains(r.Failures, f => f.ReasonCode == ReasonCodes.SchemaUnsupported);
        Assert.Equal(0, await Count("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public async Task Wrong_header_rejects_the_delivery_at_ingestion()
    {
        var m = Manifest(SourceNames.TransferInstructions, count: 0, totals: []);
        await Register(m, Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n"));
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(DeliveryState.Rejected, o.State);
        Assert.Contains(o.Failures, f => f.ReasonCode == ReasonCodes.SchemaUnsupported);
        Assert.Equal(0, await Count("SELECT count(*) FROM transfer_instructions"));
    }

    [Fact]
    public async Task Sequence_gap_is_rejected()
    {
        var a = Manifest(SourceNames.BankingTransactions, id: "B1", seq: 1);
        await Register(a);
        await _ingestor.IngestAsync("B1");
        var next = a with { DeliveryId = "B3", BusinessDate = a.BusinessDate.AddDays(1), SequenceNumber = 3 };
        var r = await Register(next, Encoding.UTF8.GetBytes("x\n"));
        Assert.Equal(DeliveryState.Rejected, r.State);
        Assert.Contains(r.Failures, f => f.ReasonCode == ReasonCodes.SequenceGap);
    }

    [Fact]
    public async Task Missing_evidence_rejects_instead_of_failing_silently()
    {
        var m = Manifest(SourceNames.TransferInstructions);
        await Register(m);
        var path = _evidence.PathFor((await _registry.GetAsync(m.DeliveryId))!.Fingerprint);
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(DeliveryState.Rejected, o.State);
        Assert.Contains(o.Failures, f => f.ReasonCode == ReasonCodes.EvidenceMissing);
    }

    [Fact]
    public async Task Missing_mandatory_sources_are_reported_until_each_is_ingested()
    {
        var date = new DateOnly(2026, 10, 27);
        Assert.Equal(8, (await _registry.FindMissingMandatorySourcesAsync("LE-CZ-001", date)).Count);
        var m = Manifest(SourceNames.TransferInstructions);
        await Register(m);
        Assert.Equal(8, (await _registry.FindMissingMandatorySourcesAsync("LE-CZ-001", date)).Count); // registered but not ingested
        await _ingestor.IngestAsync(m.DeliveryId);
        var missing = await _registry.FindMissingMandatorySourcesAsync("LE-CZ-001", date);
        Assert.Equal(7, missing.Count);
        Assert.All(missing, x => Assert.Equal(ReasonCodes.SourceMissing, x.ReasonCode));
        Assert.DoesNotContain(missing, x => x.SourceName == SourceNames.TransferInstructions);
    }

    [Fact]
    public async Task Unknown_legal_entity_and_excess_precision_are_rejected_not_repaired()
    {
        var content = Encoding.UTF8.GetBytes(TrfHeader + "\n" +
            TrfRow("001", "10", "GBP").Replace("LE-CZ-001", "LE-XX-999") + "\n" +
            TrfRow("002", "1.1234567", "GBP") + "\n" + TrfRow("003", "5", "GBP") + "\n");
        var m = Manifest(SourceNames.TransferInstructions, count: 3, totals: [new(16.1234567m, "GBP")]);
        await Register(m, content);
        var o = await _ingestor.IngestAsync(m.DeliveryId);
        Assert.Equal(1, o.AcceptedCount);
        Assert.Equal(2, o.RejectedCount);
        Assert.Equal(DeliveryState.Qualified, o.State);
        Assert.Equal(2, await Count("SELECT count(*) FROM delivery_rejections WHERE reason_code='INVALID_VALUE'"));
        Assert.Equal(5.000000m, await _f.Scalar<decimal>("SELECT amount FROM transfer_instructions"));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_registration_and_ingestion()
    {
        var m = Manifest(SourceNames.TransferInstructions);
        _audit.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Register(m));
        Assert.Equal(0, await Count("SELECT count(*) FROM source_deliveries"));
        Assert.Equal(0, await Count("SELECT count(*) FROM jobs"));

        _audit.Armed = false;
        await Register(m);
        _audit.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _ingestor.IngestAsync(m.DeliveryId));
        Assert.Equal(0, await Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(0, await Count("SELECT count(*) FROM delivery_rejections"));
        Assert.Equal("RECEIVED", await StateOf(m.DeliveryId));
        Assert.Equal(0, await Count("SELECT count(*) FROM source_deliveries WHERE ingested_at IS NOT NULL"));
        _audit.Armed = false;
        Assert.Equal(DeliveryState.Qualified, (await _ingestor.IngestAsync(m.DeliveryId)).State); // retry succeeds
    }

    [Fact]
    public async Task Module_wires_the_job_queue_to_ingestion_end_to_end()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Evidence:Directory"] = _dir }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton(_f.DataSource);
        sc.AddSingleton<Microsoft.Extensions.Logging.ILogger<JobProcessor>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>.Instance);
        sc.AddAudit();
        sc.AddSingleton<ISourceAdapter, FakeTransferAdapter>();
        sc.AddIngestion(cfg);
        await using var sp = sc.BuildServiceProvider();

        var m = Manifest(SourceNames.TransferInstructions);
        var obj = await sp.GetRequiredService<EvidenceStore>().WriteAsync(new MemoryStream(Files[m.SourceName]));
        await sp.GetRequiredService<DeliveryRegistry>().RegisterAsync(new DeliveryRegistration(m, obj.Sha256, SourceFormat.Csv, "LE-CZ-001"));
        Assert.True(await sp.GetRequiredService<JobProcessor>().ProcessOneAsync("w1", default));

        Assert.Equal("QUALIFIED", await StateOf(m.DeliveryId));
        Assert.Equal("SUCCEEDED", await _f.Scalar<string>("SELECT state FROM jobs WHERE type='delivery.ingest'"));
        Assert.Equal(4, await Count("SELECT count(*) FROM transfer_instructions"));
    }
}
