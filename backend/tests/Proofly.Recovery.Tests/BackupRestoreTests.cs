using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Proofly.Data;
using Proofly.Data.Audit;
using Proofly.Data.Evidence;
using Proofly.Data.Exceptions.Commands;
using Proofly.Data.Idempotency;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Data.Safeguarding.Commands;
using Proofly.Recovery.Tests.Support;

namespace Proofly.Recovery.Tests;

/// <summary>
/// The customer backup/restore procedure (deploy/customer/README.md): pg_dump -Fc of the database plus a copy of the evidence
/// directory taken while the worker is stopped, restored with pg_restore --clean --if-exists into a fresh database and a
/// fresh directory. The restored system must hold the same data, a verifiable audit chain, resolvable evidence, identical
/// run history and result hashes, a no-op migration run, and must keep working (new audit events, new jobs, new deliveries).
/// </summary>
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private static readonly DateOnly Day = Seed.Date;
    private const string Le = "LE-REC-B";
    private RecoveryDb _source = null!;
    private RecoveryDb? _restored;
    private readonly string _evidence = TempDir.Create("proofly-rec-bk-src");
    private readonly string _evidenceCopy = TempDir.Create("proofly-rec-bk-copy");
    private readonly string _dumpDir = TempDir.Create("proofly-rec-bk-dump");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dumpDir);
        _source = await RecoveryDb.CreateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        if (_restored is not null) await _restored.DisposeAsync();
        TempDir.Delete(_evidence);
        TempDir.Delete(_evidenceCopy);
        TempDir.Delete(_dumpDir);
    }

    private sealed record Populated(string RunId, string PackSha, string DeliverySha, string AdjustmentId);

    /// <summary>Real ingestion (evidence file + records), a processed run with exceptions, a READY evidence pack, a pending approval, a saved view, an idempotency key and a queued job.</summary>
    private async Task<Populated> Populate()
    {
        await Seed.Users(_source);
        await Seed.RunInputs(_source, Le);
        await _source.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-CZ-001','Test','EUR')");
        using var rig = new Rig(_source, _evidence);

        var file = TransferFile.Build("2026-10-26");
        var reg = await rig.RegisterAsync(file, "DEL-TRF-0926", new DateOnly(2026, 10, 26));
        Assert.True(reg.Created);
        Assert.True(await rig.PollAsync("w1")); // delivery.ingest through the real job processor
        var deliverySha = (await _source.Scalar<string>("SELECT fingerprint_sha256 FROM source_deliveries WHERE id='DEL-TRF-0926'"))!.Trim();

        var run = await rig.Get<RunProcessor>().ProcessAsync(new ProcessRunPayload(Le, Day));
        var cmds = rig.Get<IAdjustmentCommands>();
        var adj = await cmds.ProposeAsync(new ProposeAdjustmentCommand(run.RunId!, "u-prep", "GBP", 100m, "documented timing difference", ["EV-1"], Day, null), default);
        Assert.Equal(Proofly.Data.Safeguarding.Commands.CommandOutcome.Ok, adj.Outcome);

        Assert.Equal(RequestOutcome.Created, (await rig.Get<IEvidencePackCommands>().RequestAsync(run.RunId!, "u-prep", "req-1")).Outcome);
        Assert.True(await rig.PollAsync("w1")); // evidence_pack.generate
        var pack = await rig.Get<IEvidencePackCommands>().GetDownloadableAsync(run.RunId!);
        Assert.NotNull(pack);

        await rig.Get<IExceptionCommandRepository>().CreateViewAsync("u-owner", "Open critical", """{"status":["OPEN"]}""", "-dueAt");
        var store = rig.Get<IIdempotencyStore>();
        await store.BeginAsync("u-prep", "key-restore", "a".PadRight(64, 'a'), TimeSpan.FromHours(24), default);
        await store.CompleteAsync("u-prep", "key-restore", new StoredResponse(201, "application/json", new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{\"ok\":true}"), "req-2"), default);
        await rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-29"), DedupeKey: "queued-at-backup", RunAfter: DateTimeOffset.UtcNow.AddHours(2)));
        return new Populated(run.RunId!, pack!.Sha256, deliverySha, adj.AdjustmentId!);
    }

    private static async Task<SortedDictionary<string, string>> Digests(RecoveryDb db)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in (await db.TableCounts()).Keys)
        {
            result[table] = (await db.Scalar<string>($"SELECT coalesce(md5(string_agg(x, '|' ORDER BY x)), '') FROM (SELECT row_to_json(t)::text AS x FROM \"{table}\" t) s"))!;
        }
        return result;
    }

    [RequiresPgToolsFact]
    public async Task Dump_and_restore_into_a_fresh_database_keeps_data_audit_chain_evidence_and_run_history()
    {
        var p = await Populate();
        var counts = await _source.TableCounts();
        var digests = await Digests(_source);
        var audit = await new AuditVerifier(_source.DataSource).VerifyAsync();
        Assert.True(audit.IsValid);
        Assert.True(audit.EventsChecked > 10);
        var runHashes = await _source.Scalar<string>("SELECT string_agg(id||':'||run_version||':'||state||':'||result_hash, ',' ORDER BY id) FROM runs");
        Assert.Contains("RUN-" + Le, runHashes);

        // --- backup (worker stopped: no rig is alive), exactly as the operator procedure does it ---
        var dump = Path.Combine(_dumpDir, "proofly.dump");
        await PgTools.DumpAsync(_source.Database, dump);
        TempDir.Copy(_evidence, _evidenceCopy);
        Assert.True(new FileInfo(dump).Length > 0);

        // --- disaster: the original database and evidence directory are gone ---
        await _source.DisposeAsync();
        TempDir.Delete(_evidence);

        // --- restore ---
        _restored = await RecoveryDb.CreateDatabaseAsync(migrate: false);
        await PgTools.RestoreAsync(_restored.Database, dump);
        await _restored.Restart();

        Assert.Equal(counts, await _restored.TableCounts());
        Assert.Equal(digests, await Digests(_restored)); // every row of every table, byte for byte as JSON
        Assert.Equal(runHashes, await _restored.Scalar<string>("SELECT string_agg(id||':'||run_version||':'||state||':'||result_hash, ',' ORDER BY id) FROM runs"));

        var verification = await new AuditVerifier(_restored.DataSource).VerifyAsync();
        Assert.True(verification.IsValid);
        Assert.Equal(audit.EventsChecked, verification.EventsChecked);

        // Evidence files resolve by SHA-256 from the restored copy, for every stored reference in the database.
        var store = new EvidenceStore(new EvidenceOptions { Directory = _evidenceCopy });
        Assert.True(await store.VerifyAsync(p.DeliverySha));
        Assert.True(await store.VerifyAsync(p.PackSha));
        var pack = await new EvidencePackCommands(_restored.DataSource, new JobStore(_restored.DataSource), new AuditWriter()).GetDownloadableAsync(p.RunId);
        Assert.Equal(p.PackSha, pack!.Sha256);
        Assert.True(await store.VerifyAsync(pack.Sha256));

        // Migrations re-applied after restore are a no-op (the procedure runs `migrate` after every restore).
        Assert.Equal(0, await new MigrationRunner(_restored.DataSource).RunAsync());

        // The restored system keeps working on the same chain, sequences and queue.
        using var rig = new Rig(_restored, _evidenceCopy);
        Assert.Equal(IdempotencyOutcome.Replay, (await rig.Get<IIdempotencyStore>().BeginAsync("u-prep", "key-restore", "a".PadRight(64, 'a'), TimeSpan.FromHours(24), default)).Outcome);
        Assert.Equal("PENDING", await _restored.Scalar<string>("SELECT status FROM approvals"));
        Assert.Single(await rig.Get<IExceptionCommandRepository>().ListViewsAsync("u-owner"));
        Assert.Equal(1L, (await rig.Store.CountsByStateAsync())[JobState.Queued]);

        var decided = await rig.Get<IAdjustmentCommands>().DecideAsync(new DecideAdjustmentCommand(p.AdjustmentId, "u-signer", true, "approved after restore", 1, 1, null), default);
        Assert.Equal(Proofly.Data.Safeguarding.Commands.CommandOutcome.Ok, decided.Outcome);
        await _restored.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-AFTER','After restore','EUR')");
        var enq = await rig.Store.EnqueueAsync(new JobRequest(AdjustmentCommands.ExpireJobType, new ExpireAdjustmentsPayload("2026-10-30"), DedupeKey: "after-restore"));
        Assert.True(enq.Created); // job id sequence continues, no key collision
        var next = await rig.RegisterAsync(TransferFile.Build("2026-10-30"), "DEL-TRF-1030", new DateOnly(2026, 10, 30));
        Assert.True(next.Created);
        Assert.True(await rig.PollAsync("w-restored"));
        var after = await new AuditVerifier(_restored.DataSource).VerifyAsync();
        Assert.True(after.IsValid);
        Assert.True(after.EventsChecked > verification.EventsChecked);
    }

    [RequiresPgToolsFact]
    public async Task Restore_over_a_populated_database_with_clean_replaces_it_with_the_backup_state()
    {
        // pg_restore --clean --if-exists is the documented way to restore in place: work done after the backup is discarded.
        await Populate();
        var counts = await _source.TableCounts();
        var dump = Path.Combine(_dumpDir, "proofly.dump");
        await PgTools.DumpAsync(_source.Database, dump);

        await _source.Exec("INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-LATER','Added after backup','EUR')");
        await _source.Exec("INSERT INTO jobs (type, payload, dedupe_key) VALUES ('x', '{}', 'after-backup')");

        await PgTools.RestoreAsync(_source.Database, dump);
        await _source.Restart();

        Assert.Equal(counts, await _source.TableCounts());
        Assert.True((await new AuditVerifier(_source.DataSource).VerifyAsync()).IsValid);
        Assert.Equal(0, await new MigrationRunner(_source.DataSource).RunAsync());
    }

    [Fact]
    public void Pg_tools_are_found_or_the_backup_tests_are_skipped_with_a_reason()
    {
        // Guards the skip logic: when the tools exist the Skip reason must be null, otherwise it must explain what is missing.
        var attr = new RequiresPgToolsFactAttribute();
        if (PgTools.Available) Assert.Null(attr.Skip);
        else Assert.Contains("pg_dump", attr.Skip);
    }
}
