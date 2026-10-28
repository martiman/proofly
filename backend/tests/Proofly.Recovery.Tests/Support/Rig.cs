using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Proofly.Connectors.Abstractions;
using Proofly.Data;
using Proofly.Data.Audit;
using Proofly.Data.Evidence;
using Proofly.Data.Exceptions.Commands;
using Proofly.Data.Idempotency;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Data.Safeguarding.Commands;
using Proofly.Worker.Orchestration;

namespace Proofly.Recovery.Tests.Support;

/// <summary>Audit writer that fails (or cancels) on its Nth append: the database transaction around it must roll back completely.</summary>
public sealed class FailOnNthAudit(IAuditWriter inner, int failOnCall = int.MaxValue, CancellationTokenSource? cancel = null) : IAuditWriter
{
    private int _calls;
    public int FailOnCall { get; set; } = failOnCall;
    public int Calls => _calls;

    public Task<AuditEventRecord> AppendAsync(NpgsqlTransaction tx, AuditEventInput input, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _calls) != FailOnCall) return inner.AppendAsync(tx, input, ct);
        if (cancel is null) throw new InvalidOperationException("simulated crash while writing audit");
        cancel.Cancel();
        return inner.AppendAsync(tx, input, ct); // throws OperationCanceledException
    }
}

/// <summary>
/// The worker-side object graph over one <see cref="RecoveryDb"/>: the same classes and handlers the Worker registers,
/// wired by hand so tests can interpose failures. A new Rig over the same database is a restarted process.
/// </summary>
public sealed class Rig : IDisposable
{
    public ServiceProvider Services { get; }
    public CsvTransferAdapter Adapter { get; } = new();
    public FailOnNthAudit Audit { get; }
    public string EvidenceDir { get; }

    public Rig(RecoveryDb db, string evidenceDir, int failAuditOn = int.MaxValue, CancellationTokenSource? cancelOnFail = null,
        IReadOnlyList<string>? mandatory = null, bool runEnqueuer = false)
    {
        EvidenceDir = evidenceDir;
        Audit = new FailOnNthAudit(new AuditWriter(), failAuditOn, cancelOnFail);
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(db.DataSource);
        s.AddSingleton<IAuditWriter>(Audit);
        s.AddSingleton<IAuditVerifier, AuditVerifier>();
        s.AddJobs(o => { o.BackoffBase = TimeSpan.Zero; o.PollInterval = TimeSpan.FromMilliseconds(20); });
        s.AddSingleton(new IngestionOptions { MandatorySources = mandatory ?? Seed.Mandatory });
        s.AddSingleton(new RunProcessingOptions());
        s.AddSingleton(new EvidenceOptions { Directory = evidenceDir });
        s.AddSingleton<EvidenceStore>();
        s.AddSingleton<ISourceAdapter>(Adapter);
        s.AddSingleton<ISourceAdapterRegistry>(sp => new SourceAdapterRegistry(sp.GetServices<ISourceAdapter>()));
        s.AddSingleton<DeliveryRegistry>();
        s.AddSingleton<DeliveryIngestor>();
        s.AddSingleton<RunProcessor>();
        if (runEnqueuer) s.AddSingleton<RunEnqueuer>();
        s.AddSingleton<EvidencePackBuilder>();
        s.AddSingleton<IEvidencePackCommands, EvidencePackCommands>();
        s.AddSafeguardingCommandsData();
        s.AddExceptionCommandsData();
        s.AddSingleton<IIdempotencyStore>(sp => new PostgresIdempotencyStore(db.DataSource, TimeProvider.System));
        s.AddSingleton(NullLogger<RunProcessJob>.Instance);
        s.AddJobHandler<IngestDeliveryJob, IngestDeliveryPayload>(IngestionJobs.IngestDelivery);
        s.AddJobHandler<RunProcessJob, ProcessRunPayload>(RunJobs.ProcessRun);
        s.AddJobHandler<EvidencePackJobHandler, GenerateEvidencePackPayload>(EvidencePackJobs.Generate);
        s.AddJobHandler<ExpireAdjustmentsHandler, ExpireAdjustmentsPayload>(AdjustmentCommands.ExpireJobType);
        Services = s.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public JobStore Store => Get<JobStore>();
    public EvidenceStore Evidence => Get<EvidenceStore>();

    /// <summary>One job-processor poll as the worker host does it (recovers expired leases, claims, runs, completes/fails).</summary>
    public Task<bool> PollAsync(string worker) => Get<JobProcessor>().ProcessOneAsync(worker, CancellationToken.None);

    /// <summary>Registers the file as evidence and as a delivery (enqueues delivery.ingest).</summary>
    public async Task<RegistrationResult> RegisterAsync(TransferFile file, string deliveryId, DateOnly date)
    {
        var obj = await Evidence.WriteAsync(new MemoryStream(file.Bytes));
        return await Get<DeliveryRegistry>().RegisterAsync(new DeliveryRegistration(file.Manifest(deliveryId, date), obj.Sha256, SourceFormat.Csv, "LE-CZ-001"));
    }

    public void Dispose() => Services.Dispose();
}

public static class TempDir
{
    public static string Create(string prefix) => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");

    /// <summary>Deletes a directory tree including read-only evidence objects.</summary>
    public static void Delete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    public static void Copy(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }
}
