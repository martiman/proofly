using System.Globalization;
using Npgsql;
using Proofly.Connectors.Abstractions;
using Proofly.Connectors.Csv;
using Proofly.Data.Audit;
using Proofly.Data.Governance;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Domain;
using Proofly.Domain.Enums;
using Proofly.Domain.Records;

namespace Proofly.Data.Fixtures;

/// <param name="Directory">A dataset directory: <c>delivery_manifest.csv</c> and the CSV files it names (see examples/synthetic-data).</param>
/// <param name="EvidenceDirectory">Where the delivered files are stored (content-addressed evidence store).</param>
/// <param name="LegalEntityId">Entity to register the deliveries for. Null: the single legal_entity_id found in the data files.</param>
/// <param name="BaseCurrency">Base currency of the entity row when it has to be created. Null: the currency with most client account balances.</param>
/// <param name="QualifyCountMismatch">True (default): a declared-record-count mismatch QUALIFIES the delivery (records kept, discrepancy raised as an exception) instead of
/// REJECTING it (the production default, see <see cref="SourcePolicy"/>). The dataset seeds a count mismatch on the bank evidence and still expects the
/// matching and lifecycle findings that need that evidence; a rejected delivery would turn them into NO_CANDIDATE noise.</param>
/// <param name="ProcessRun">False loads the read-side evidence only (deliveries and records) and does not run the controls.</param>
public sealed record DatasetLoadOptions(
    string Directory, string EvidenceDirectory, string? LegalEntityId = null, string? BaseCurrency = null, bool ProcessRun = true, bool QualifyCountMismatch = true);

public sealed record LoadedDelivery(
    string DeliveryId, string SourceName, string FileName, bool Registered, DeliveryState? RegistrationState, int? DeclaredRecordCount,
    IngestionOutcome? Ingestion, IReadOnlyList<DeliveryFailure> Failures);

public sealed record DatasetLoadResult(
    string LegalEntityId, DateOnly BusinessDate, IReadOnlyList<LoadedDelivery> Deliveries, RunProcessingOutcome? Run);

/// <summary>
/// Loads a synthetic dataset directory through the REAL delivery registry, ingestor, CSV adapters and run processor (no shortcut
/// writes): <c>delivery_manifest.csv</c> drives the deliveries (delivery id, declared record count and per-currency totals via
/// <see cref="Proofly.Connectors.ManifestTotals"/>, expected_by/received_at, correction links) and every manifest row's file is stored as evidence,
/// registered and ingested; then <c>run.process</c> runs for the business date. Rows are never repaired: invalid rows land in
/// <c>delivery_rejections</c> exactly as in production. Idempotent: a repeat finds the same delivery ids and fingerprints, ingestion
/// and the run report "already" and nothing is written twice. The legal entity row is created when absent (id from the data files).
/// Development and test fixture only; the dataset must be synthetic.
/// </summary>
public static class SyntheticDatasetLoader
{
    public const string ManifestFile = "delivery_manifest.csv";

    public static async Task<DatasetLoadResult> LoadAsync(NpgsqlDataSource db, DatasetLoadOptions o, CancellationToken ct = default)
    {
        var adapters = new SourceAdapterRegistry(CsvModule.CreateAdapters());
        var manifest = await ReadManifestAsync(adapters, o.Directory, ct);
        var date = manifest.Select(m => m.BusinessDate).Distinct().ToList() is [var single]
            ? single : throw new InvalidOperationException("The manifest must name exactly one business date.");

        var entity = o.LegalEntityId ?? await DiscoverEntityAsync(adapters, o.Directory, manifest, ct);
        var baseCurrency = o.BaseCurrency ?? await DiscoverBaseCurrencyAsync(adapters, o.Directory, manifest, ct);
        await using (var cmd = db.CreateCommand("""
            INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES (@id, @name, @ccy, ARRAY[@ccy])
            ON CONFLICT (id) DO NOTHING
            """))
        {
            cmd.Parameters.AddWithValue("id", entity);
            cmd.Parameters.AddWithValue("name", $"Synthetic dataset {entity}");
            cmd.Parameters.AddWithValue("ccy", baseCurrency);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await BaselineRuleset.InstallAsync(db, ct);

        var audit = new AuditWriter();
        var jobs = new JobStore(db);
        var opts = new IngestionOptions();
        if (o.QualifyCountMismatch) opts.Default.CountMismatch = MismatchOutcome.Qualify;
        var store = new EvidenceStore(new EvidenceOptions { Directory = o.EvidenceDirectory });
        var registry = new DeliveryRegistry(db, audit, jobs, adapters, opts);
        var ingestor = new DeliveryIngestor(db, audit, store, adapters, opts);

        var loaded = new List<LoadedDelivery>();
        foreach (var m in manifest)
        {
            var path = Path.Combine(o.Directory, m.FileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Manifest row {m.DeliveryId} names {m.FileName}, which is not in {o.Directory}.", path);
            EvidenceObject obj;
            await using (var fs = File.OpenRead(path)) obj = await store.WriteAsync(fs, ct);
            var reg = await registry.RegisterAsync(new DeliveryRegistration(m, obj.Sha256, SourceFormat.Csv, entity), ct);
            IngestionOutcome? ing = null;
            if (reg.State is not DeliveryState.Rejected && !reg.IdConflict)
                ing = await ingestor.IngestAsync(m.DeliveryId, ct);
            loaded.Add(new LoadedDelivery(m.DeliveryId, m.SourceName, m.FileName, reg.Created, reg.State, m.DeclaredRecordCount, ing, reg.Failures));
        }

        RunProcessingOutcome? run = null;
        if (o.ProcessRun)
            run = await new RunProcessor(db, audit, opts, new RunProcessingOptions()).ProcessAsync(new ProcessRunPayload(entity, date));
        return new DatasetLoadResult(entity, date, loaded, run);
    }

    private static async Task<List<DeliveryManifestEntry>> ReadManifestAsync(ISourceAdapterRegistry adapters, string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, ManifestFile);
        if (!File.Exists(path)) throw new FileNotFoundException($"{ManifestFile} not found in {dir}.", path);
        var adapter = adapters.Resolve<DeliveryManifestEntry>(SourceNames.DeliveryManifest, SourceFormat.Csv);
        var descriptor = new SourceDescriptor(SourceNames.DeliveryManifest, SourceFormat.Csv, "1.0", "manifest", default, ManifestFile);
        await using var fs = File.OpenRead(path);
        var check = await adapter.ValidateSchemaAsync(fs, descriptor, ct);
        if (!check.IsValid) throw new InvalidOperationException($"{ManifestFile}: {check.Message}");
        fs.Position = 0;
        var list = new List<DeliveryManifestEntry>();
        await foreach (var r in adapter.ParseAsync(fs, descriptor, ct))
        {
            if (r.Record is null)
                throw new InvalidOperationException($"{ManifestFile} row {r.RowNumber} is invalid: {r.Rejection?.ReasonCode} {r.Rejection?.Message}");
            list.Add(r.Record);
        }
        if (list.Count == 0) throw new InvalidOperationException($"{ManifestFile} has no rows.");
        return list;
    }

    private static async Task<List<TRecord>> ReadAcceptedAsync<TRecord>(
        ISourceAdapterRegistry adapters, string dir, IEnumerable<DeliveryManifestEntry> manifest, string source, CancellationToken ct)
        where TRecord : class, ISourceRecord
    {
        var result = new List<TRecord>();
        var m = manifest.FirstOrDefault(x => x.SourceName == source);
        if (m is null) return result;
        var adapter = adapters.Resolve<TRecord>(source, SourceFormat.Csv);
        var d = new SourceDescriptor(source, SourceFormat.Csv, m.SchemaVersion, m.DeliveryId, m.BusinessDate, m.FileName);
        await using var fs = File.OpenRead(Path.Combine(dir, m.FileName));
        await foreach (var r in adapter.ParseAsync(fs, d, ct)) if (r.Record is not null) result.Add(r.Record);
        return result;
    }

    /// <summary>The one legal_entity_id the accepted rows of the data files carry; several distinct ids need an explicit option.</summary>
    private static async Task<string> DiscoverEntityAsync(ISourceAdapterRegistry adapters, string dir, List<DeliveryManifestEntry> manifest, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in await ReadAcceptedAsync<AccountBalance>(adapters, dir, manifest, SourceNames.ClientAccountBalances, ct)) ids.Add(r.LegalEntityId!);
        foreach (var r in await ReadAcceptedAsync<TransferInstruction>(adapters, dir, manifest, SourceNames.TransferInstructions, ct)) ids.Add(r.LegalEntityId!);
        foreach (var r in await ReadAcceptedAsync<SafeguardingBalance>(adapters, dir, manifest, SourceNames.SafeguardingBalances, ct)) ids.Add(r.LegalEntityId!);
        ids.RemoveWhere(string.IsNullOrWhiteSpace);
        return ids.Count == 1 ? ids.Single()
            : throw new InvalidOperationException(ids.Count == 0 ? "No legal_entity_id found in the data files; pass --legal-entity."
                : $"Several legal entities in the data files ({string.Join(", ", ids.Order())}); pass --legal-entity.");
    }

    private static async Task<string> DiscoverBaseCurrencyAsync(ISourceAdapterRegistry adapters, string dir, List<DeliveryManifestEntry> manifest, CancellationToken ct)
    {
        var balances = await ReadAcceptedAsync<AccountBalance>(adapters, dir, manifest, SourceNames.ClientAccountBalances, ct);
        return balances.GroupBy(b => b.Balance.Currency).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault() ?? "EUR";
    }
}
