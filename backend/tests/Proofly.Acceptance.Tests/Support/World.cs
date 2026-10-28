extern alias worker;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Proofly.Api.Auth;
using Proofly.Connectors.Abstractions;
using Proofly.Data;
using Proofly.Data.Audit;
using Proofly.Data.Auth;
using Proofly.Data.Evidence;
using Proofly.Data.Idempotency;
using Proofly.Data.Ingestion;
using Proofly.Data.Jobs;
using Proofly.Data.Orchestration;
using Proofly.Data.Safeguarding.Commands;
using Proofly.Domain.Enums;
using RunProcessJob = worker::Proofly.Worker.Orchestration.RunProcessJob;

namespace Proofly.Acceptance.Tests.Support;

/// <summary>One HTTP exchange: status, parsed JSON body (problem+json for errors) and the headers the tests look at.</summary>
public sealed record Resp(HttpStatusCode Status, JsonElement Body, string? ETag, bool Replayed, string? Location, string RawBody)
{
    public int Code => (int)Status;
    public string? ProblemType => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("type", out var t) ? t.GetString()?.Split('/').Last() : null;
    public JsonElement this[string name] => Body.GetProperty(name);
    public string Str(string name) => Body.GetProperty(name).GetString()!;
    /// <summary>Asserts the status code and shows the response body when it differs.</summary>
    public Resp Is(int code) { Assert.True(Code == code, $"expected HTTP {code} but got {Code}: {RawBody}"); return this; }
}

/// <summary>
/// The system under acceptance: a migrated per-test schema in proofly_test, the real API host (Auth:Mode=Local, one token per user) and the
/// worker-side handlers wired as the Worker registers them, driven one job at a time with <see cref="JobProcessor.ProcessOneAsync"/>.
/// </summary>
public sealed class World : IAsyncLifetime
{
    public const string Server = "Host=localhost;Port=55432;Username=proofly;Database=proofly_test";
    public const string SigningKey = "unit-test-signing-key-ONLY-0123456789abcdef-0123456789abcdef-0123456789";
    public static readonly string[] Mandatory = ["transfer_instructions", "internal_ledger_entries", "client_account_balances", "banking_transactions", "safeguarding_balances"];
    public static readonly DateOnly Day = new(2026, 10, 27);

    private static readonly JsonSerializerOptions Wire = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string Schema { get; } = "t_" + Guid.NewGuid().ToString("N");
    public string EvidenceDir { get; } = Path.Combine(Path.GetTempPath(), "proofly-acc-" + Guid.NewGuid().ToString("N"));
    public NpgsqlDataSource Db { get; private set; } = null!;
    private NpgsqlDataSource _workerDb = null!;
    private ApiFactory _factory = null!;
    private ServiceProvider _worker = null!;
    private readonly Dictionary<string, string> _tokens = [];
    private readonly Dictionary<string, HttpClient> _clients = [];

    private string Cs(int pool) => $"{Server};Search Path={Schema};Maximum Pool Size={pool}";

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(Server))
        {
            await using var c = admin.CreateCommand($"CREATE SCHEMA \"{Schema}\"");
            await c.ExecuteNonQueryAsync();
        }
        Db = NpgsqlDataSource.Create(Cs(3));
        await new MigrationRunner(Db).RunAsync();
        Directory.CreateDirectory(EvidenceDir);

        _workerDb = NpgsqlDataSource.Create(Cs(3));
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(_workerDb);
        s.AddSingleton<IAuditWriter, AuditWriter>();
        s.AddSingleton<IAuditVerifier, AuditVerifier>();
        s.AddJobs(o => { o.BackoffBase = TimeSpan.Zero; o.PollInterval = TimeSpan.FromMilliseconds(20); });
        s.AddSingleton(new IngestionOptions { MandatorySources = Mandatory });
        s.AddSingleton(new RunProcessingOptions());
        s.AddSingleton(new EvidenceOptions { Directory = EvidenceDir });
        s.AddSingleton<EvidenceStore>();
        s.AddSingleton<ISourceAdapter>(new TransferCsvAdapter());
        s.AddSingleton<ISourceAdapterRegistry>(sp => new SourceAdapterRegistry(sp.GetServices<ISourceAdapter>()));
        s.AddSingleton<DeliveryRegistry>();
        s.AddSingleton<DeliveryIngestor>();
        s.AddSingleton<RunProcessor>();
        s.AddSingleton<RunEnqueuer>();
        s.AddSingleton<EvidencePackBuilder>();
        s.AddSingleton<IEvidencePackCommands, EvidencePackCommands>();
        s.AddSafeguardingCommandsData();
        s.AddSingleton<IIdempotencyStore>(_ => new PostgresIdempotencyStore(_workerDb, TimeProvider.System));
        s.AddSingleton(NullLogger<RunProcessJob>.Instance);
        s.AddJobHandler<IngestDeliveryJob, IngestDeliveryPayload>(IngestionJobs.IngestDelivery);
        s.AddJobHandler<RunProcessJob, ProcessRunPayload>(RunJobs.ProcessRun);
        s.AddJobHandler<EvidencePackJobHandler, GenerateEvidencePackPayload>(EvidencePackJobs.Generate);
        _worker = s.BuildServiceProvider();

        _factory = new ApiFactory(Cs(4), EvidenceDir);
        _ = _factory.Server;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients.Values) { c.Dispose(); }
        await _factory.DisposeAsync();
        await _worker.DisposeAsync();
        await _workerDb.DisposeAsync();
        await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try { Directory.Delete(EvidenceDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        await using var admin = NpgsqlDataSource.Create(Server);
        await using var c2 = admin.CreateCommand($"DROP SCHEMA \"{Schema}\" CASCADE");
        await c2.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ database helpers

    public async Task Exec(string sql) { await using var c = Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }

    public async Task<T?> Scalar<T>(string sql)
    {
        await using var c = Db.CreateCommand(sql);
        var v = await c.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    public async Task<long> Count(string sql) => await Scalar<long>(sql);

    public async Task<List<string>> Strings(string sql)
    {
        var l = new List<string>();
        await using var c = Db.CreateCommand(sql);
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync()) { l.Add(r.GetString(0)); }
        return l;
    }

    /// <summary>Serialises the named tables (optionally filtered) in a stable order: proves that something did not change.</summary>
    public async Task<string> Snapshot(params (string Table, string Where)[] tables)
    {
        var sb = new StringBuilder();
        foreach (var (t, w) in tables)
        {
            sb.Append(t).Append('=').Append(await Scalar<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text)::text,'[]') FROM {t} x WHERE {w}")).Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ users

    public async Task AddUser(string key, params (Role Role, string Le)[] roles)
    {
        await new UserDirectory(Db).EnsureUserAsync(new UserRecord("u-" + key, key, key + "@acc.test", "sub-" + key, true,
            roles.Select(r => new RoleAssignment(WireNames.ToUpperSnake(r.Role), r.Le)).ToList()));
        _tokens[key] = _factory.Services.GetRequiredService<LocalTokenService>().Issue("sub-" + key);
    }

    public static string UserId(string key) => "u-" + key;

    private HttpClient Client(string key)
    {
        if (_clients.TryGetValue(key, out var c)) { return c; }
        c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tokens[key]);
        return _clients[key] = c;
    }

    // ------------------------------------------------------------------ HTTP

    public async Task<Resp> Send(string user, HttpMethod method, string url, object? body = null, string? ifMatch = null, string? key = null, string? requestId = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) { req.Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body, Wire), Encoding.UTF8, "application/json"); }
        if (ifMatch is not null) { req.Headers.TryAddWithoutValidation("If-Match", ifMatch); }
        if (method != HttpMethod.Get) { req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString()); }
        if (requestId is not null) { req.Headers.Add("X-Request-Id", requestId); }
        using var res = await Client(user).SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        var json = raw.Length == 0 ? default : JsonDocument.Parse(raw).RootElement.Clone();
        return new Resp(res.StatusCode, json, res.Headers.ETag?.ToString(), res.Headers.Contains("Idempotent-Replayed"), res.Headers.Location?.ToString(), raw);
    }

    public Task<Resp> Get(string user, string url) => Send(user, HttpMethod.Get, url);

    public async Task<byte[]> Download(string user, string url, string? requestId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (requestId is not null) { req.Headers.Add("X-Request-Id", requestId); }
        using var res = await Client(user).SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Fresh ETag of a GET resource (the precondition every mutation sends).</summary>
    public async Task<string> ETag(string user, string url)
    {
        var r = await Get(user, url);
        Assert.Equal(200, r.Code);
        return r.ETag ?? throw new InvalidOperationException("no ETag on " + url);
    }

    // ------------------------------------------------------------------ worker

    /// <summary>A worker-side service, or a handler built the way the job processor builds it (constructor injection from the worker container).</summary>
    public T Worker<T>() where T : notnull => ActivatorUtilities.GetServiceOrCreateInstance<T>(_worker);

    public Task RunJob<THandler, TPayload>(TPayload payload) where THandler : IJobHandler<TPayload> =>
        Worker<THandler>().HandleAsync(payload, new JobContext(1, 1, 5, null, null), CancellationToken.None);

    /// <summary>Runs queued jobs one by one (the real handlers) until the queue is empty. Returns how many ran.</summary>
    public async Task<int> Drain()
    {
        var n = 0;
        var p = _worker.GetRequiredService<JobProcessor>();
        while (await p.ProcessOneAsync("acceptance-worker", CancellationToken.None)) { n++; if (n > 50) { throw new InvalidOperationException("job loop"); } }
        return n;
    }

    public async Task<string> JobStates() =>
        string.Join(",", await Strings("SELECT type || ':' || state FROM jobs ORDER BY id"));

    public async Task<AuditVerification> VerifyAudit() => await _worker.GetRequiredService<IAuditVerifier>().VerifyAsync();

    // ------------------------------------------------------------------ data: the mandatory sources of one entity and day

    /// <summary>Legal entity (GBP base, EUR and JPY) plus the four mandatory sources other than transfer_instructions as accepted canonical records.</summary>
    public async Task SeedEntity(string le)
    {
        var sources = new (string Source, int Count)[] { ("internal_ledger_entries", 2), ("client_account_balances", 3), ("banking_transactions", 2), ("safeguarding_balances", 3) };
        var sql = $"INSERT INTO legal_entities (id, name, base_currency, currencies) VALUES ('{le}','Entity {le}','GBP','{{EUR,JPY}}');\n";
        foreach (var (s, n) in sources)
        {
            sql += $"""
                INSERT INTO source_deliveries (id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, declared_record_count, received_at, state,
                                               accepted_record_count, rejected_record_count, total_row_count, ingested_at)
                VALUES ('D-{le}-{s}','{le}','{s}','2026-10-27','1.0','{s}.csv',encode(sha256('D-{le}-{s}'::bytea),'hex'),{n},'2026-10-28T06:00Z','RECEIVED',{n},0,{n},now());

                """;
        }
        string D(string s) => $"D-{le}-{s}";
        sql += $"""
            INSERT INTO ledger_entries (delivery_id, row_number, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency,
                transaction_reference, entry_type, source_status) VALUES
              ('{D("internal_ledger_entries")}',1,'L-1','A1','{le}','2026-10-27T10:00Z','2026-10-27','DEBIT',100,'GBP','TRF-A-1','TRANSFER','POSTED'),
              ('{D("internal_ledger_entries")}',2,'L-2','A2','{le}','2026-10-27T10:00Z','2026-10-27','DEBIT',5000,'JPY','TRF-A-2','TRANSFER','POSTED');
            INSERT INTO account_balances (delivery_id, row_number, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency, source_timestamp) VALUES
              ('{D("client_account_balances")}',1,'A1','{le}','2026-10-27','DAILY_ROLLFORWARD',1000,900,'GBP','2026-10-27T23:59:59Z'),
              ('{D("client_account_balances")}',2,'A2','{le}','2026-10-27','DAILY_ROLLFORWARD',20000,15000,'JPY','2026-10-27T23:59:59Z'),
              ('{D("client_account_balances")}',3,'A3','{le}','2026-10-27','DAILY_ROLLFORWARD',500,500,'EUR','2026-10-27T23:59:59Z');
            INSERT INTO bank_transactions (delivery_id, row_number, external_transaction_id, external_account_id, booking_timestamp, value_date, amount, currency,
                debit_credit_indicator, bank_reference, transaction_status, source_timestamp) VALUES
              ('{D("banking_transactions")}',1,'BTX-{le}-1','EXT-1','2026-10-27T11:00Z','2026-10-27',100,'GBP','DEBIT','REF-1','BOOKED','2026-10-28T06:00Z'),
              ('{D("banking_transactions")}',2,'BTX-{le}-2','EXT-1','2026-10-27T11:00Z','2026-10-27',5200,'JPY','DEBIT','REF-2','BOOKED','2026-10-28T06:00Z');
            INSERT INTO safeguarding_balances (delivery_id, row_number, source_row_id, external_account_id, legal_entity_id, balance_timestamp, balance_type, balance_amount, currency,
                evidence_reference, source_timestamp) VALUES
              ('{D("safeguarding_balances")}',1,'SG-1','SGA-GBP','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',800,'GBP','STMT-1','2026-10-28T06:30Z'),
              ('{D("safeguarding_balances")}',2,'SG-2','SGA-JPY','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',15000,'JPY','STMT-2','2026-10-28T06:30Z'),
              ('{D("safeguarding_balances")}',3,'SG-3','SGA-EUR','{le}','2026-10-27T23:59:59Z','CLOSING_AVAILABLE',600,'EUR','STMT-3','2026-10-28T06:30Z');
            """;
        await Exec(sql);
    }

    /// <summary>Registers the transfer file as evidence and as a delivery (enqueues delivery.ingest). The fifth mandatory source completes the set.</summary>
    public async Task<RegistrationResult> RegisterTransfers(string le, string deliveryId, string? correctionOf = null)
    {
        var file = TransferFile.Build(le, "2026-10-27", "A", corrected: correctionOf is not null);
        var obj = await _worker.GetRequiredService<EvidenceStore>().WriteAsync(new MemoryStream(file.Bytes));
        return await _worker.GetRequiredService<DeliveryRegistry>().RegisterAsync(
            new DeliveryRegistration(file.Manifest(deliveryId, Day, correctionOf), obj.Sha256, SourceFormat.Csv, le));
    }

    /// <summary>Seeds one entity, registers and ingests its transfer delivery and runs run.process: returns the v1 run id.</summary>
    public async Task<string> ProcessedRun(string le)
    {
        await SeedEntity(le);
        var reg = await RegisterTransfers(le, $"D-{le}-transfer_instructions");
        Assert.True(reg.Created, string.Join(";", reg.Failures.Select(f => f.Message)));
        await Drain();
        return RunProcessor.RunId(le, Day, 1);
    }

    /// <summary>
    /// A second (or later) entity: all five mandatory deliveries inserted as accepted canonical records, then the real run.process handler.
    /// (DeliveryRegistry keys "the current delivery" on source and date only, not on the legal entity, so a second entity cannot register
    /// the same source and day through the registry: see docs/testing/governance-acceptance.md, gaps.)
    /// </summary>
    public async Task<string> ProcessedRunSeeded(string le)
    {
        await SeedEntity(le);
        await Exec($"""
            INSERT INTO source_deliveries (id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, declared_record_count, received_at, state,
                                           accepted_record_count, rejected_record_count, total_row_count, ingested_at)
            VALUES ('D-{le}-transfer_instructions','{le}','transfer_instructions','2026-10-27','1.0','transfer_instructions.csv',encode(sha256('D-{le}-transfer_instructions'::bytea),'hex'),3,'2026-10-28T06:00Z','RECEIVED',3,0,3,now());
            INSERT INTO transfer_instructions (delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency,
                instruction_timestamp, requested_value_date, source_status, external_reference) VALUES
              ('D-{le}-transfer_instructions',1,'S-1','TRF-A-1','A1','{le}','BANK_TRANSFER',100,'GBP','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-1'),
              ('D-{le}-transfer_instructions',2,'S-2','TRF-A-2','A2','{le}','BANK_TRANSFER',5000,'JPY','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-2'),
              ('D-{le}-transfer_instructions',3,'S-3','TRF-A-3','A3','{le}','BANK_TRANSFER',40.50,'EUR','2026-10-27T09:00Z','2026-10-27','SETTLED','REF-3');
            """);
        var job = _worker.GetRequiredService<RunProcessJob>();
        await job.HandleAsync(new ProcessRunPayload(le, Day), new JobContext(1, 1, 5, null, null), CancellationToken.None);
        return RunProcessor.RunId(le, Day, 1);
    }

    private sealed class ApiFactory(string connectionString, string evidenceDir) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Proofly", connectionString);
            builder.UseSetting("Auth:Mode", "Local");
            builder.UseSetting("Auth:Local:SigningKey", SigningKey);
            builder.UseSetting("Evidence:Directory", evidenceDir);
        }
    }
}
