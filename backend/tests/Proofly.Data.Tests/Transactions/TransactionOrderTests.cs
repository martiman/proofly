using Proofly.Data.Transactions;
using Proofly.Domain.Ids;

namespace Proofly.Data.Tests.Transactions;

/// <summary>The transaction list is ordered by (timestamp, numeric row id, transaction id), the same key as its cursor: DD-47 comes before DD-111.</summary>
public class TransactionOrderTests(SchemaFixture f) : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private TransactionReadRepository Repo => new(f.DataSource);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Seeded = new();

    public Task InitializeAsync() => Seeded.GetOrAdd(f.Schema, _ => Seed());

    private async Task Seed()
    {
        await new MigrationRunner(f.DataSource).RunAsync();
        await using var c = f.DataSource.CreateCommand("""
            INSERT INTO legal_entities (id, name, base_currency) VALUES ('LE-O', 'Entity O', 'GBP');
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('RO', 'LE-O', '2026-10-27', 1, 'rs-1', 'REVIEW_REQUIRED');
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
              VALUES ('DO', 'RO', 'LE-O', 'mixed', '2026-10-27', '1', 'o.csv', repeat('c', 64), 'RECEIVED');
            -- 130 direct-debit events and 3 transfers, all at the same instant: only the row id decides.
            INSERT INTO direct_debit_events (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, amount, currency, event_type, event_timestamp, source_status)
              SELECT 'DO', 'DD-' || g, 'M-' || g, 'ACC-1', 'LE-O', 10, 'GBP', 'SUBMITTED', '2026-10-27 08:00:00+00', 'SUBMITTED' FROM generate_series(1, 130) g;
            INSERT INTO transfer_instructions (delivery_id, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status)
              SELECT 'DO', 'T-' || g, 'I-' || g, 'ACC-1', 'LE-O', 'OUTGOING', 5, 'GBP', '2026-10-27 08:00:00+00', 'SETTLED' FROM generate_series(1, 3) g;
            """);
        await c.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static long Pk(string id) => TransactionIds.TryParse(id, out _, out var pk) ? pk : throw new InvalidOperationException(id);

    [Fact]
    public async Task Order_is_numeric_in_the_row_id_and_paging_across_pages_neither_skips_nor_repeats()
    {
        var one = await Repo.ListTransactionsAsync("RO", new(), null, 500, false);
        Assert.Equal(133, one.Rows.Count);
        var expected = one.Rows.OrderBy(r => r.Timestamp).ThenBy(r => Pk(r.Id)).ThenBy(r => r.Id, StringComparer.Ordinal).Select(r => r.Id).ToList();
        Assert.Equal(expected, one.Rows.Select(r => r.Id));
        var dd = one.Rows.Select(r => r.Id).Where(id => id.StartsWith("DD-", StringComparison.Ordinal)).ToList();
        Assert.True(dd.IndexOf("DD-47") < dd.IndexOf("DD-111"), "DD-47 must come before DD-111");
        Assert.Equal(dd.Select(Pk).Order().ToList(), dd.Select(Pk).ToList());

        // Keyset paging with page sizes that cut through the 2-digit / 3-digit boundary visits every row exactly once, in the same order.
        foreach (var size in new[] { 7, 50, 64 })
        {
            var seen = new List<string>();
            TransactionCursor? cur = null;
            for (var i = 0; i < 100; i++)
            {
                var p = await Repo.ListTransactionsAsync("RO", new(), cur, size, false);
                seen.AddRange(p.Rows.Select(x => x.Id));
                if (!p.HasMore) { break; }
                cur = new TransactionCursor(p.Rows[^1].Timestamp, p.Rows[^1].Id);
            }
            Assert.Equal(expected, seen);
        }
    }

    [Fact]
    public async Task Order_and_paging_hold_with_an_enriched_filter()
    {
        // A match-status filter takes the other query path (the whole run is enriched first); the order and the cursor are the same.
        var filter = new TransactionFilter(MatchStatus: "UNMATCHED");
        var one = await Repo.ListTransactionsAsync("RO", filter, null, 500, false);
        var seen = new List<string>();
        TransactionCursor? cur = null;
        for (var i = 0; i < 100; i++)
        {
            var p = await Repo.ListTransactionsAsync("RO", filter, cur, 40, false);
            seen.AddRange(p.Rows.Select(x => x.Id));
            if (!p.HasMore) { break; }
            cur = new TransactionCursor(p.Rows[^1].Timestamp, p.Rows[^1].Id);
        }
        Assert.Equal(one.Rows.Select(r => r.Id), seen);
        Assert.Equal(133, seen.Count);
        var dd = seen.Where(id => id.StartsWith("DD-", StringComparison.Ordinal)).Select(Pk).ToList();
        Assert.Equal(dd.Order().ToList(), dd);
    }
}
