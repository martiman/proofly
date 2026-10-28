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
        // GBP: transfer DEBIT 100 (instruction = DEBIT), bank DEBIT 100, ledger DEBIT 100 + 7, card CREDIT 10.50, ledger CREDIT 500
        Assert.Equal(307.00m, gbp.DebitTotal);
        Assert.Equal(10000m, r.Aggregates.Single(a => a.Currency == "JPY").CreditTotal); // direct debit events = CREDIT
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
        Assert.Equal("transfer_instructions:" + one.Id[4..], one.RecordId); // "{sourceName}:{pk}", pk shared with the TRF- id
        Assert.Equal("DEBIT", one.Direction);
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
        Assert.Equal(new LineageDelivery("D1", new string('a', 64), "1", null), Assert.Single(l.Deliveries));
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
        Assert.Equal((120m, -10m), (a2.Independent, a2.Difference)); // difference = independent - recorded
        Assert.Equal("REVIEW_REQUIRED", a2.State);
        Assert.Equal("FAILED", p.Rows[2].State); // blocking exception on the account itself
        Assert.Equal(-10m, p.Aggregates.Single(a => a.Currency == "EUR").TotalDifference);
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
        var a = Assert.Single(await Repo.FindAccountAsync("R1", "ACC-1", null));
        var p1 = await Repo.ListLedgerEntriesAsync("R1", "ACC-1", a.Currency, null, 2);
        Assert.True(p1.HasMore);
        var p2 = await Repo.ListLedgerEntriesAsync("R1", "ACC-1", a.Currency, new LedgerCursor(p1.Rows[^1].Timestamp, p1.Rows[^1].InternalId), 2);
        Assert.Equal("internal_ledger_entries:" + p2.Rows[0].Id[4..], p2.Rows[0].RecordId);
        Assert.Equal("PEND-1", p2.Rows[0].Reference);
        Assert.Equal(["X-1"], p2.Rows[0].ExceptionIds);
        Assert.False(p2.HasMore);
        var ex = await Repo.ListExceptionsAsync("R1", ["X-1"]);
        Assert.Equal("Pending fee", Assert.Single(ex).Title);
        Assert.Empty(await Repo.FindAccountAsync("R2", "ACC-1", null));
    }

    [Fact]
    public async Task Accounts_read_the_controls_persisted_result_instead_of_recomputing()
    {
        await f.Exec("""
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('R3','LE-A','2026-10-27',2,'rs-1','REVIEW_REQUIRED');
            INSERT INTO account_control_results (run_id, account_id, currency, opening, credits, debits, adjustments, independent, recorded, difference, opening_supported, rule_version) VALUES
              ('R3','ACC-1','GBP',1000,500,100,3,1403,1390,13,true,'ledger/1'),
              ('R3','ACC-1','EUR',NULL,0,0,0,NULL,NULL,NULL,false,'ledger/1'),
              ('R3','ACC-2','GBP',0,0,0,0,0,0,0,true,'ledger/1');
            """);

        var page = await Repo.ListAccountsAsync("R3", new(), null, 10, default);
        Assert.Equal(["ACC-1/EUR", "ACC-1/GBP", "ACC-2/GBP"], page.Rows.Select(a => a.AccountId + "/" + a.Currency).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var gbp = page.Rows.Single(a => a.AccountId == "ACC-1" && a.Currency == "GBP");
        Assert.Equal((1000m, 1390m, 1403m, 13m, 3m), (gbp.Opening, gbp.Recorded, gbp.Independent, gbp.Difference, gbp.Adjustments)); // the control's numbers, not a SQL recomputation
        Assert.Equal("REVIEW_REQUIRED", gbp.State);
        var eur = page.Rows.Single(a => a.Currency == "EUR");
        Assert.Null(eur.Independent); // the control could not compute it: null, not zero
        Assert.Null(eur.Difference);
        Assert.Equal("REVIEW_REQUIRED", eur.State);
        Assert.Equal("PASSED", page.Rows.Single(a => a.AccountId == "ACC-2").State);
        Assert.Equal(13m, page.Aggregates.Single(a => a.Currency == "GBP").TotalDifference);

        // An account in several currencies is addressed by currency.
        Assert.Equal(2, (await Repo.FindAccountAsync("R3", "ACC-1", null)).Count);
        Assert.Equal("GBP", Assert.Single(await Repo.FindAccountAsync("R3", "ACC-1", "GBP")).Currency);
        Assert.Empty(await Repo.FindAccountAsync("R3", "ACC-1", "JPY"));

        // R1 has no persisted result: the documented fallback still applies.
        Assert.Equal(1400m, Assert.Single(await Repo.FindAccountAsync("R1", "ACC-1", null)).Independent);
    }

    [Fact]
    public async Task Explicit_exception_links_reach_transactions_and_ledger_entries()
    {
        await f.Exec("""
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('R4','LE-A','2026-10-28',1,'rs-1','REVIEW_REQUIRED');
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
              VALUES ('D4','R4','LE-A','mixed','2026-10-28','1','d.csv', repeat('d', 64), 'RECEIVED');
            INSERT INTO transfer_instructions (delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status)
              VALUES ('D4', 7, 'T-L', 'I-L', 'ACC-L', 'LE-A', 'OUTGOING', 9, 'GBP', '2026-10-28 09:00:00+00', 'SETTLED');
            INSERT INTO ledger_entries (delivery_id, ledger_entry_id, account_id, legal_entity_id, booking_timestamp, value_date, debit_credit_indicator, amount, currency, entry_type, source_status)
              VALUES ('D4', 'L-L', 'ACC-L', 'LE-A', '2026-10-28 09:10:00+00', '2026-10-28', 'DEBIT', 9, 'GBP', 'TRANSFER', 'POSTED');
            INSERT INTO account_balances (delivery_id, account_id, legal_entity_id, balance_date, balance_type, opening_balance_amount, balance_amount, currency)
              VALUES ('D4', 'ACC-L', 'LE-A', '2026-10-28', 'DAILY_ROLLFORWARD', 0, -9, 'GBP');
            INSERT INTO exceptions (id, run_id, legal_entity_id, business_date, title, control, reason_code, severity, due_at)
              VALUES ('X-LINK', 'R4', 'LE-A', '2026-10-28', 'Linked, not row-matched', 'independent_ledger', 'LINKED', 'LOW', '2026-10-30 00:00:00+00');
            INSERT INTO exception_links (exception_id, record_table, record_id)
              SELECT 'X-LINK', 'transfer_instructions', id FROM transfer_instructions WHERE source_row_id = 'T-L'
              UNION ALL SELECT 'X-LINK', 'ledger_entries', id FROM ledger_entries WHERE ledger_entry_id = 'L-L';
            """);

        var tx = (await Repo.ListTransactionsAsync("R4", None, null, 10, false)).Rows;
        Assert.All(tx, t => Assert.Equal(["X-LINK"], t.ExceptionIds));
        Assert.Equal(7, tx.Single(t => t.Kind == "BANK_TRANSFER").RowNumber); // the file row recorded at ingestion
        Assert.Null(tx.Single(t => t.Kind == "LEDGER_ENTRY").RowNumber);      // not recorded: null
        var entry = (await Repo.ListLedgerEntriesAsync("R4", "ACC-L", "GBP", null, 10)).Rows.Single();
        Assert.Equal(["X-LINK"], entry.ExceptionIds);
        var acc = Assert.Single(await Repo.FindAccountAsync("R4", "ACC-L", null));
        Assert.Equal(["X-LINK"], acc.ExceptionIds);
        var lineage = (await Repo.GetLineageAsync("R4", tx.Single(t => t.Kind == "BANK_TRANSFER").Id))!;
        Assert.Equal(7, Assert.Single(lineage.Deliveries).RowNumber);
    }

    [Fact]
    public async Task Record_and_transaction_ids_resolve_in_every_area()
    {
        await f.Exec("""
            INSERT INTO runs (id, legal_entity_id, business_date, run_version, ruleset_version, state) VALUES ('R5','LE-A','2026-10-29',1,'rs-1','REVIEW_REQUIRED');
            INSERT INTO source_deliveries (id, run_id, legal_entity_id, source_name, business_date, schema_version, file_name, fingerprint_sha256, state)
              VALUES ('D6','R5','LE-A','transfer_instructions','2026-10-29','1','t.csv', repeat('f', 64), 'RECEIVED');
            INSERT INTO transfer_instructions (delivery_id, row_number, source_row_id, instruction_id, client_account_id, legal_entity_id, transaction_type, amount, currency, instruction_timestamp, source_status)
              VALUES ('D6', 2, 'T-ID', 'I-ID', 'ACC-I', 'LE-A', 'OUTGOING', 3, 'GBP', '2026-10-29 09:00:00+00', 'SETTLED');
            """);
        var tx = (await Repo.ListTransactionsAsync("R5", None, null, 10, false)).Rows.Single();
        Assert.Matches(@"^TRF-\d+$", tx.Id);
        Assert.Matches(@"^transfer_instructions:\d+$", tx.RecordId);

        // the record id the transaction exposes is the one the source-record endpoint resolves
        var found = await new Proofly.Data.Sources.SourceReadRepository(f.DataSource).GetRecordAsync(tx.RecordId!, Proofly.Data.Sources.EntityScope.Everything);
        var record = found!.Value.Record;
        Assert.Equal(("T-ID", 2, true), (record.SourceRowId, record.RowNumber, record.Accepted));
        var recordId = tx.RecordId!;
        Assert.Equal(tx.Id[4..], recordId[(recordId.IndexOf(':') + 1)..]); // one primary key behind both ids

        // the transaction id is what matching accepts as a focus
        var focus = await new Proofly.Data.Matching.MatchingRepository(f.DataSource).ResolveFocusAsync("R5", tx.Id, null, default);
        Assert.Equal((null, tx.Id, Proofly.Data.Matching.FocusFailure.None), focus);
        Assert.Equal(tx.Id, (await Repo.GetTransactionAsync("R5", tx.Id))!.Id);
    }
}
