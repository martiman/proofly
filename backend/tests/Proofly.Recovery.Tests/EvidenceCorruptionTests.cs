using Proofly.Data.Audit;
using Proofly.Data.Evidence;
using Proofly.Data.Ingestion;
using Proofly.Connectors.Abstractions;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Recovery.Tests.Support;

namespace Proofly.Recovery.Tests;

/// <summary>An altered, truncated or missing evidence object is detected by its SHA-256 and reported, never silently used.</summary>
public sealed class EvidenceCorruptionTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 10, 27);
    private RecoveryDb _db = null!;
    private Rig _rig = null!;
    private readonly string _dir = TempDir.Create("proofly-rec-corrupt");

    public async Task InitializeAsync()
    {
        _db = await RecoveryDb.CreateSchemaAsync();
        await _db.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-CZ-001','Test','EUR')");
        _rig = new Rig(_db, _dir);
    }

    public async Task DisposeAsync()
    {
        _rig.Dispose();
        await _db.DisposeAsync();
        TempDir.Delete(_dir);
    }

    private static void FlipOneByte(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public async Task A_flipped_byte_fails_verification_and_the_intact_object_still_verifies()
    {
        var a = await _rig.Evidence.WriteAsync(new MemoryStream(TransferFile.Build("2026-10-27").Bytes));
        var b = await _rig.Evidence.WriteAsync(new MemoryStream(TransferFile.Build("2026-10-28").Bytes));
        Assert.True(await _rig.Evidence.VerifyAsync(a.Sha256));

        FlipOneByte(a.Path);

        Assert.False(await _rig.Evidence.VerifyAsync(a.Sha256));
        Assert.True(await _rig.Evidence.VerifyAsync(b.Sha256));
    }

    [Fact]
    public async Task Truncated_and_missing_objects_fail_verification()
    {
        var a = await _rig.Evidence.WriteAsync(new MemoryStream(TransferFile.Build("2026-10-27").Bytes));
        var b = await _rig.Evidence.WriteAsync(new MemoryStream(TransferFile.Build("2026-10-28").Bytes));
        File.SetAttributes(a.Path, FileAttributes.Normal);
        File.WriteAllBytes(a.Path, File.ReadAllBytes(a.Path)[..10]);
        File.SetAttributes(b.Path, FileAttributes.Normal);
        File.Delete(b.Path);

        Assert.False(await _rig.Evidence.VerifyAsync(a.Sha256));
        Assert.False(await _rig.Evidence.VerifyAsync(b.Sha256));
    }

    [Fact]
    public async Task Ingestion_of_a_delivery_whose_stored_file_was_altered_is_rejected_with_a_visible_reason_and_stores_no_records()
    {
        var file = TransferFile.Build("2026-10-27");
        var obj = await _rig.Evidence.WriteAsync(new MemoryStream(file.Bytes));
        await _rig.Get<DeliveryRegistry>().RegisterAsync(new DeliveryRegistration(file.Manifest("DEL-TRF-01", Day), obj.Sha256, SourceFormat.Csv, "LE-CZ-001"));
        FlipOneByte(obj.Path);

        var outcome = await _rig.Get<DeliveryIngestor>().IngestAsync("DEL-TRF-01");

        Assert.Equal(DeliveryState.Rejected, outcome.State);
        Assert.Contains(outcome.Failures, f => f.ReasonCode == ReasonCodes.EvidenceMissing);
        Assert.Equal(0, await _db.Count("SELECT count(*) FROM transfer_instructions"));
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM delivery_rejections WHERE delivery_id='DEL-TRF-01' AND reason_code='EVIDENCE_MISSING'"));
        Assert.Equal("REJECTED", await _db.Scalar<string>("SELECT state FROM source_deliveries"));
        Assert.Contains("EVIDENCE_MISSING", await _db.Scalar<string>("SELECT reason FROM audit_events WHERE event_type='DELIVERY_RECEIVED' ORDER BY id DESC LIMIT 1"));
        Assert.True((await _rig.Get<IAuditVerifier>().VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task A_corrupted_evidence_pack_is_detected_by_the_download_integrity_check_and_the_refusal_is_audited()
    {
        await Seed.Users(_db);
        await Seed.RunInputs(_db, "LE-REC-C");
        var run = await _rig.Get<Proofly.Data.Orchestration.RunProcessor>().ProcessAsync(new(("LE-REC-C"), Seed.Date));
        var commands = _rig.Get<IEvidencePackCommands>();
        await commands.RequestAsync(run.RunId!, "u-prep", null);
        Assert.True(await _rig.PollAsync("w1"));
        var pack = (await commands.GetDownloadableAsync(run.RunId!))!;
        Assert.True(await _rig.Evidence.VerifyAsync(pack.Sha256));

        FlipOneByte(_rig.Evidence.PathFor(pack.Sha256));

        Assert.False(await _rig.Evidence.VerifyAsync(pack.Sha256)); // what GET /runs/{id}/evidence-pack checks before sending a byte
        await commands.RecordDownloadAsync(pack, "u-prep", "req-9", "INTEGRITY_FAILURE");
        Assert.Equal(1, await _db.Count("SELECT count(*) FROM audit_events WHERE event_type='EVIDENCE_PACK_DOWNLOADED' AND after->>'outcome'='INTEGRITY_FAILURE'"));
        Assert.True((await _rig.Get<IAuditVerifier>().VerifyAsync()).IsValid);
    }
}
