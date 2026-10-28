using Npgsql;
using Proofly.Data.Audit;
using Proofly.Domain.Enums;

namespace Proofly.Data.Tests.Audit;

public class AuditCanonicalTests
{
    [Fact]
    public void Hash_is_stable_across_json_key_order_and_whitespace()
    {
        string H(string before) => AuditCanonical.Sha256Hex(AuditCanonical.Canonicalize(
            "*", 1, DateTimeOffset.Parse("2026-10-27T10:00:00.123456Z"), "RUN_STARTED", "SYSTEM", null, "sys",
            "RUN", "r1", "r1", null, null, "s", null, before, null, null));
        Assert.Equal(H("""{"a":1,"b":{"x":"1","y":[2,1]}}"""), H("""{ "b": {"y": [2,1], "x":"1"}, "a": 1 }"""));
        Assert.NotEqual(H("""{"a":1}"""), H("""{"a":2}"""));
    }

    [Fact]
    public void Exponent_numbers_are_rejected() =>
        Assert.Throws<ArgumentException>(() => AuditCanonical.CanonicalJson("""{"a":1e5}"""));
}

public class AuditStoreTests : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private readonly SchemaFixture _f;
    private readonly AuditWriter _w = new();
    public AuditStoreTests(SchemaFixture f) => _f = f;

    public async Task InitializeAsync() => await new MigrationRunner(_f.DataSource).RunAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AuditEventInput Ev(string subject, string chain = AuditChains.Global, string type = "RUN_STARTED", string? run = "run-1") =>
        new(type, AuditActor.System(), "RUN", subject, "Run " + subject, Reason: "because", RunId: run,
            RequestId: "req-1", BeforeJson: """{"state":"A"}""", AfterJson: """{"state":"B","amount":"10.50"}""", ChainId: chain);

    private async Task Append(AuditEventInput e)
    {
        await using var c = await _f.DataSource.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await _w.AppendAsync(tx, e);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Chain_is_continuous_and_verifies()
    {
        const string chain = "le-continuity";
        for (var i = 0; i < 4; i++) await Append(Ev("s" + i, chain));
        var v = await new AuditVerifier(_f.DataSource).VerifyAsync(chain);
        Assert.True(v.IsValid);
        Assert.Equal(4, v.EventsChecked);
        Assert.Equal(3, await _f.Scalar<long>(
            $"SELECT count(*) FROM audit_events e JOIN audit_events p ON p.chain_id=e.chain_id AND p.chain_seq=e.chain_seq-1 AND p.hash=e.previous_hash WHERE e.chain_id='{chain}'"));
    }

    [Fact]
    public async Task Concurrent_writers_produce_a_valid_chain()
    {
        const string chain = "le-concurrent";
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => Append(Ev("c" + i, chain)))));
        var v = await new AuditVerifier(_f.DataSource).VerifyAsync(chain);
        Assert.True(v.IsValid, v.FirstBreak?.Problem);
        Assert.Equal(40, v.EventsChecked);
        Assert.Equal(40L, await _f.Scalar<long>($"SELECT max(chain_seq) FROM audit_events WHERE chain_id='{chain}'"));
    }

    [Fact]
    public async Task Rollback_of_business_transaction_leaves_no_audit_row()
    {
        const string chain = "le-rollback";
        await Append(Ev("keep", chain));
        await using (var c = await _f.DataSource.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await _w.AppendAsync(tx, Ev("lost", chain));
            await tx.RollbackAsync();
        }
        Assert.Equal(1, await _f.Scalar<long>($"SELECT count(*) FROM audit_events WHERE chain_id='{chain}'"));
        await Append(Ev("next", chain));
        Assert.True((await new AuditVerifier(_f.DataSource).VerifyAsync(chain)).IsValid);
    }

    [Fact]
    public async Task Update_and_delete_are_blocked()
    {
        await Append(Ev("immutable", "le-immutable"));
        var up = await Assert.ThrowsAsync<PostgresException>(() => _f.Exec("UPDATE audit_events SET reason='x' WHERE chain_id='le-immutable'"));
        Assert.Equal("23001", up.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => _f.Exec("DELETE FROM audit_events WHERE chain_id='le-immutable'"));
    }

    [Fact]
    public async Task Tamper_is_detected_and_first_broken_link_reported()
    {
        const string chain = "le-tamper";
        for (var i = 0; i < 4; i++) await Append(Ev("t" + i, chain));
        await _f.Exec("ALTER TABLE audit_events DISABLE TRIGGER trg_audit_no_update_delete");
        try
        {
            await _f.Exec($"UPDATE audit_events SET summary='forged' WHERE chain_id='{chain}' AND chain_seq=2");
        }
        finally { await _f.Exec("ALTER TABLE audit_events ENABLE TRIGGER trg_audit_no_update_delete"); }
        var v = await new AuditVerifier(_f.DataSource).VerifyAsync(chain);
        Assert.False(v.IsValid);
        Assert.Equal(2, v.FirstBreak!.ChainSeq);
    }

    [Fact]
    public async Task Removed_tail_is_detected_through_the_chain_head()
    {
        const string chain = "le-tail";
        for (var i = 0; i < 3; i++) await Append(Ev("x" + i, chain));
        await _f.Exec("ALTER TABLE audit_events DISABLE TRIGGER trg_audit_no_update_delete");
        try { await _f.Exec($"DELETE FROM audit_events WHERE chain_id='{chain}' AND chain_seq=3"); }
        finally { await _f.Exec("ALTER TABLE audit_events ENABLE TRIGGER trg_audit_no_update_delete"); }
        Assert.False((await new AuditVerifier(_f.DataSource).VerifyAsync(chain)).IsValid);
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        await using var c = await _f.DataSource.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _w.AppendAsync(tx, Ev("s") with { Type = "NOPE" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _w.AppendAsync(tx, Ev("s") with { Reason = "x" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _w.AppendAsync(tx, Ev("s") with { AfterJson = """{"n":1e3}""" }));
    }

    [Fact]
    public async Task Reader_filters_and_pages_with_keyset_cursor()
    {
        const string chain = "le-read";
        for (var i = 0; i < 5; i++) await Append(Ev("rd" + i, chain, run: "run-read"));
        await Append(new AuditEventInput("NOTE_ADDED", new AuditActor(ActorType.User, "u1", "Alice"), "EXCEPTION", "ex-9",
            "Note about needle", RunId: "run-read", ChainId: chain));
        var reader = new AuditReader(_f.DataSource);

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await reader.ListAsync(new AuditQuery(RunId: "run-read", Limit: 2, Cursor: cursor));
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            Assert.Equal(cursor is not null, page.HasMore);
        } while (cursor is not null);
        Assert.Equal(6, seen.Count);
        Assert.Equal(seen.OrderByDescending(x => x), seen);

        Assert.Single((await reader.ListAsync(new AuditQuery(RunId: "run-read", ActorType: ActorType.User))).Items);
        Assert.Single((await reader.ListAsync(new AuditQuery(RunId: "run-read", SubjectKind: "EXCEPTION"))).Items);
        Assert.Single((await reader.ListAsync(new AuditQuery(Search: "NEEDLE"))).Items);
        Assert.Empty((await reader.ListAsync(new AuditQuery(RunId: "run-read", From: DateTimeOffset.UtcNow.AddHours(1)))).Items);
        Assert.Equal(6, (await reader.ListAsync(new AuditQuery(RunId: "run-read", To: DateTimeOffset.UtcNow.AddHours(1)))).Items.Count);
    }
}
