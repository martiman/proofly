using Proofly.Data.Transactions;

namespace Proofly.Data.Tests.Transactions;

public class TransactionReadRepositoryTests(SchemaFixture f) : IClassFixture<SchemaFixture>, IAsyncLifetime
{
    private TransactionReadRepository Repo => new(f.DataSource);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Seeded = new();

    public Task InitializeAsync() => Seeded.GetOrAdd(f.Schema, async _ =>
    {
        await new MigrationRunner(f.DataSource).RunAsync();
        await TxSeed.ApplyAsync(f.DataSource);
    });
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TransactionFilter None = new();

    [Fact]
    public async Task Unifies_all_five_kinds_scoped_to_the_run()
    {
        var r = await Repo.ListTransactionsAsync("R1", None, null, 100, true);
        Assert.Equal(10, r.Rows.Count);
        Assert.Equal(10, r.Total);
        Assert.Equal(["BANK_EVIDENCE", "BANK_TRANSFER", "CARD", "DIRECT_DEBIT", "LEDGER_ENTRY"], r.Rows.Select(x => x.Kind).Distinct().Order().ToArray());
        Assert.DoesNotContain(r.Rows, x => x.Reference == "I-9");
        Assert.Equal(r.Rows.OrderBy(x => x.Timestamp).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id), r.Rows.Select(x => x.Id));
    }

    [Fact]
    public async Task Aggregates_are_per_currency_over_the_whole_filtered_set_not_the_page()
    {
        var r = await Repo.ListTransactionsAsync("R1", None, null, 2, false);
        Assert.Equal(2, r.Rows.Count);
        Assert.True(r.HasMore);
        Assert.Null(r.Total);
        var gbp = r.Aggregates.Single(a => a.Currency == "GBP");
        // GBP: bank DEBIT 100, ledger DEBIT 100 + 7, card CREDIT 10.50, ledger CREDIT 500; transfer has no direction
        Assert.Equal(207.00m, gbp.DebitTotal);
        Assert.Equal(510.50m, gbp.CreditTotal);
        Assert.Equal(["EUR", "GBP", "JPY"], r.Aggregates.Select(a => a.Currency).ToArray());
        Assert.Equal(10, r.Aggregates.Sum(a => a.Count));
    }

    [Fact]
    public async Task Cursor_paging_visits_every_row_once()
    {
        var seen = new List<string>();
        TransactionCursor? cur = null;
        for (var i = 0; i < 20; i++)
        {
            var p = await Repo.ListTransactionsAsync("R1", None, cur, 4, false);
            seen.AddRange(p.Rows.Select(x => x.Id));
            if (!p.HasMore) { break; }
            cur = new TransactionCursor(p.Rows[^1].Timestamp, p.Rows[^1].Id);
        }
        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct().Count());
    }

    [Fact]
    public async Task Filters_work()
    {
        Assert.All((await Repo.ListTransactionsAsync("R1", new(Kind: "LEDGER_ENTRY"), null, 50, false)).Rows, r => Assert.Equal("LEDGER_ENTRY", r.Kind));
        Assert.Equal(4, (await Repo.ListTransactionsAsync("R1", new(Kind: "LEDGER_ENTRY"), null, 50, false)).Rows.Count);
        Assert.Equal(2, (await Repo.ListTransactionsAsync("R1", new(MatchStatus: "MATCHED"), null, 50, false)).Rows.Count);
        Assert.Equal(2, (await Repo.ListTransactionsAsync("R1", new(Currency: "JPY"), null, 50, false)).Rows.Count);
        Assert.Equal(2, (await Repo.ListTransactionsAsync("R1", new(HasException: true), null, 50, false)).Rows.Count);
        Assert.Equal(8, (await Repo.ListTransactionsAsync("R1", new(HasException: false), null, 50, false)).Rows.Count);
        Assert.Equal(6, (await Repo.ListTransactionsAsync("R1", new(AccountId: "ACC-1"), null, 50, false)).Rows.Count);
        var window = await Repo.ListTransactionsAsync("R1", new(From: DateTimeOffset.Parse("2026-10-27T09:00:00Z"), To: DateTimeOffset.Parse("2026-10-27T10:00:00Z")), null, 50, false);
        Assert.All(window.Rows, r => Assert.InRange(r.Timestamp, DateTimeOffset.Parse("2026-10-27T09:00:00Z"), DateTimeOffset.Parse("2026-10-27T10:00:00Z")));
        Assert.Equal(["BANK_EVIDENCE", "BANK_TRANSFER"], (await Repo.ListTransactionsAsync("R1", new(Search: "ext-1"), null, 50, false)).Rows.Select(r => r.Kind).Order().ToArray());
        Assert.Empty((await Repo.ListTransactionsAsync("R1", new(Search: "nothing-like-this"), null, 50, false)).Rows);
        Assert.Empty((await Repo.ListTransactionsAsync("R-NONE", None, null, 50, false)).Rows);
    }

    [Fact]
    public async Task Detail_carries_lifecycle_match_and_exceptions()
    {
        var dd = (await Repo.ListTransactionsAsync("R1", new(Kind: "DIRECT_DEBIT"), null, 10, false)).Rows;
        Assert.Equal(["SUBMITTED", "SETTLED"], dd[0].Lifecycle);
        var tr = (await Repo.ListTransactionsAsync("R1", new(Kind: "BANK_TRANSFER"), null, 10, false)).Rows.Single(r => r.SourceRowId == "T-1");
        var one = (await Repo.GetTransactionAsync("R1", tr.Id))!;
        Assert.Equal("MATCHED", one.MatchStatus);
        Assert.Equal("MG-1", one.MatchGroupId);
        Assert.Equal("D1/T-1", one.RecordId);
        var card = (await Repo.ListTransactionsAsync("R1", new(Kind: "CARD"), null, 10, false)).Rows.Single();
        Assert.Equal(["X-3"], card.ExceptionIds);
        Assert.True(card.Accepted);
        Assert.Equal("UNMATCHED", card.MatchStatus);
        Assert.Null(await Repo.GetTransactionAsync("R2", tr.Id));
        Assert.Null(await Repo.GetTransactionAsync("R1", "TRF-999999"));
    }

    [Fact]
    public async Task Lineage_links_related_records_deliveries_and_exceptions()
    {
        var tr = (await Repo.ListTransactionsAsync("R1", new(Kind: "BANK_TRANSFER"), null, 10, false)).Rows.Single(r => r.SourceRowId == "T-1");
        var l = (await Repo.GetLineageAsync("R1", tr.Id))!;
        Assert.Contains(l.Nodes, n => n.Kind == "BANK_TRANSACTION" && n.SourceRowId == "B-1");
        Assert.Contains(l.Nodes, n => n.Kind == "LEDGER_ENTRY" && n.SourceRowId == "L-2");
        Assert.DoesNotContain(l.Nodes, n => n.SourceRowId == "L-1");
        Assert.Equal(new LineageDelivery("D1", new string('a', 64), "1"), Assert.Single(l.Deliveries));
        Assert.Contains(l.Edges, e => e.Relation == "DELIVERED");
        Assert.Null(await Repo.GetLineageAsync("R2", tr.Id));
    }

    [Fact]
    public async Task Accounts_compute_independent_balance_from_posted_ledger_entries_per_currency()
    {
        var p = await Repo.ListAccountsAsync("R1", new(), null, 10, default);
        Assert.Equal(["ACC-1", "ACC-2", "ACC-3"], p.Rows.Select(a => a.AccountId).ToArray());
        var a1 = p.Rows[0];
        Assert.Equal((1000m, 1400m, 1400m, 0m), (a1.Opening, a1.Recorded, a1.Independent, a1.Difference));
        Assert.Equal("REVIEW_REQUIRED", a1.State); // open exception X-1 on its ledger entry
        Assert.Equal(["X-1", "X-3"], a1.ExceptionIds); // X-3 (accepted) sits on a card event of the account
        var a2 = p.Rows[1];
        Assert.Equal((120m, 10m), (a2.Independent, a2.Difference));
        Assert.Equal("REVIEW_REQUIRED", a2.State);
        Assert.Equal("FAILED", p.Rows[2].State); // blocking exception on the account itself
        Assert.Equal(10m, p.Aggregates.Single(a => a.Currency == "EUR").TotalDifference);
        Assert.Equal(1, p.Aggregates.Single(a => a.Currency == "GBP").Accounts);
    }

    [Fact]
    public async Task Account_filters_and_paging()
    {
        Assert.Equal(["ACC-2"], (await Repo.ListAccountsAsync("R1", new(Currency: "EUR"), null, 10, default)).Rows.Select(a => a.AccountId).ToArray());
        Assert.Equal(["ACC-3"], (await Repo.ListAccountsAsync("R1", new(State: "FAILED"), null, 10, default)).Rows.Select(a => a.AccountId).ToArray());
        Assert.Equal(["ACC-1"], (await Repo.ListAccountsAsync("R1", new(Search: "acc-1"), null, 10, default)).Rows.Select(a => a.AccountId).ToArray());
        var first = await Repo.ListAccountsAsync("R1", new(), null, 2, default);
        Assert.True(first.HasMore);
        var second = await Repo.ListAccountsAsync("R1", new(), new AccountCursor(first.Rows[^1].AccountId, first.Rows[^1].Currency), 2, default);
        Assert.Equal(["ACC-3"], second.Rows.Select(a => a.AccountId).ToArray());
        Assert.False(second.HasMore);
        Assert.Empty((await Repo.ListAccountsAsync("R-NONE", new(), null, 10, default)).Rows);
    }

    [Fact]
    public async Task Ledger_entries_page_and_exceptions_load()
    {
        var a = (await Repo.GetAccountAsync("R1", "ACC-1"))!;
        var p1 = await Repo.ListLedgerEntriesAsync("R1", "ACC-1", a.Currency, null, 2);
        Assert.True(p1.HasMore);
        var p2 = await Repo.ListLedgerEntriesAsync("R1", "ACC-1", a.Currency, new LedgerCursor(p1.Rows[^1].Timestamp, p1.Rows[^1].InternalId), 2);
        Assert.Equal(["L-3"], p2.Rows.Select(e => e.RecordId![3..]).ToArray());
        Assert.Equal(["X-1"], p2.Rows[0].ExceptionIds);
        Assert.False(p2.HasMore);
        var ex = await Repo.ListExceptionsAsync("R1", ["X-1"]);
        Assert.Equal("Pending fee", Assert.Single(ex).Title);
        Assert.Null(await Repo.GetAccountAsync("R2", "ACC-1"));
    }
}
