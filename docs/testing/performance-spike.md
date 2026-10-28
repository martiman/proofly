# Performance spike: 100,000-transaction run and reprocess on one machine (issue #48)

Status: measured on one developer machine with synthetic data. Nothing here is a statement about a customer environment.
The acceptance plan ([omvp-acceptance-plan.md](omvp-acceptance-plan.md#performance)) still requires the representative test in the
target deployment before any performance claim is published; this document does not replace it.

Harness: `backend/tools/Proofly.Benchmarks` (console tool, in the solution under `tools`, not run by `dotnet test`).
Raw results of the runs quoted here are reproducible with the commands in [Reproducing](#reproducing).

## Environment

| Item | Value |
|---|---|
| CPU | Apple M4, 10 cores |
| RAM | 24 GiB |
| Disk | internal SSD (APFS), local PostgreSQL data directory on it |
| OS | macOS (Darwin 25.6.0) |
| Runtime | .NET SDK 10.0.102, Release build, workstation GC, API host and benchmark in one process |
| PostgreSQL | 14.18 (Homebrew, aarch64), localhost:55432, trust auth, dedicated database `proofly_bench` (dropped and recreated per run) |
| PostgreSQL configuration | stock: `shared_buffers` 128 MB, `work_mem` 4 MB, `effective_cache_size` 4 GB, `max_wal_size` 1 GB, `wal_level` replica, `fsync` on, `synchronous_commit` on, autovacuum on. Not tuned. |
| `pg_stat_statements` | available but not preloaded on this server (changing it needs a restart of a server shared with other work), so statement timings come from the Npgsql driver's `ActivitySource` (every command timed, aggregated per statement text) |
| Load on the machine | not isolated: other development processes ran on the same machine during the runs. Repeat runs differed by up to about 30% on the slower endpoints (control room p50 539 ms in one 100% run, 782 ms in the final one). |
| Network | none: client, API and database are on loopback |

## Methodology

1. **Generate** (deterministic, `Random(20261027)`): CSV delivery files in the column layout of `examples/synthetic-data/2026-10-27`, written to a
   temp directory outside the repository. One legal entity, GBP/EUR/JPY. Transactions are split 45% transfers, 20% direct debits, 35% card journeys
   (authorisation + presentment, settled in batches of about 50 with one bank credit each); every transfer and direct debit also has a ledger entry and
   a bank transaction. Accounts: 45% GBP, 40% EUR, 15% JPY, opening and recorded closing balances for every account, 3 safeguarding accounts per currency.
   Seeded defects (at 100%: counts in the run log): about 0.5% missing required fields and about 0.5% duplicate rows per transaction file, about 0.5% bank
   amounts that differ from the instruction (value mismatch), 1% submitted transfers and 0.3% settled transfers without bank evidence (unmatched), 0.3%
   settled transfers without a ledger entry, 0.2% accounts with a wrong recorded balance, one safeguarding row without evidence reference, JPY covered at
   97% (shortfall), GBP short because that row is rejected.
2. **Ingest**: each delivery goes through `EvidenceStore` -> `DeliveryRegistry.RegisterAsync` -> `DeliveryIngestor.IngestAsync` (the real registry,
   framework checks, control totals, record tables, audit). The manifest is built from the generated file (declared count and per-currency totals over all rows).
   *CSV adapter*: `Proofly.Connectors/Csv` was an empty folder (another agent was building it) when the spike started, so the benchmark carries its own
   minimal streaming adapters (`CsvAdapters.cs`: exact header check, required fields, decimal/date/enum validation, reject with reason, never repair).
   Once the production adapter existed, the 100% ingest was repeated with it (`--connectors-csv`): same accepted/rejected counts per file, same run result
   hash, 13.2 s instead of 12.0 s for the eight deliveries. The tables below use the benchmark's own adapters.
3. **run.process** end to end: `RunProcessor.ProcessAsync` (load, five controls, persist, audit, one transaction). Phase times come from `Proofly.Runs` activities.
4. **Reprocess** with unchanged evidence: `RunReprocessService.RequestAsync` (new version) then `ProcessAsync` of the new version, including carrying over
   every exception of the predecessor with its history. The result hash of v2 equals that of v1 in every run.
5. **API reads** against the populated database (two run versions): a real Kestrel host on a free loopback port with the production module list and
   Local auth, `HttpClient`s with 4 concurrent clients, 5 warm-up requests, then up to 400 requests or 8 s per endpoint. p50/p95/p99 are client-side
   wall clock per request.
6. **Size and SQL**: `pg_database_size` after each stage, `pg_total_relation_size` per table, statement timings per stage.

Measured per stage: wall clock, peak working set of the benchmark process (sampled every 25 ms; includes the API host and any garbage not yet
collected, excludes the PostgreSQL server processes) and peak managed heap, database size.

## Results (after the fixes below)

Three scales of the same generator: 10% = 10,000 accounts and transactions, 50% = 50,000, 100% = 100,000 accounts and 100,000 transactions.

### Volume

| | 10% | 50% | 100% |
|---|---|---|---|
| Accounts / transactions | 10,000 / 10,000 | 50,000 / 50,000 | 100,000 / 100,000 |
| Source rows in the 8 files | 36,593 | 183,097 | 366,053 |
| CSV size | 4 MB | 21 MB | 43 MB |
| Accepted / rejected rows at ingest | 36,450 / 143 | 182,391 / 706 | 364,583 / 1,470 |
| Exceptions per run version | 13,684 | 67,750 | 136,364 |
| Match groups per run version | 26,214 | 131,038 | 262,229 |

The exception volume is high because of a behaviour of the matching control described under [Findings not fixed](#findings-not-fixed): 97% of the
exceptions are `NO_CANDIDATE` on bank rows. Seeded defects at 100% (rows): transfer duplicates 224, transfers without client account 238, submitted
transfers without bank row 475, settled transfers without bank row 141 and without ledger entry 159, transfer bank value mismatch 221, direct-debit
duplicates 105, without mandate 98, bank value mismatch 115, ledger duplicates 184, ledger rows without account 236, bank duplicates 196, card
duplicates 182, settlement without destination 9, accounts with a wrong recorded balance 191, safeguarding row without evidence reference 1.

### Stage times, memory, database size

| Stage | 10%: time | 10%: peak WS | 50%: time | 50%: peak WS | 100%: time | 100%: peak WS | 100%: DB size after |
|---|---|---|---|---|---|---|---|
| 1 generate | 0.0 s | 97 MB | 0.1 s | 99 MB | 0.2 s | 102 MB | 11 MB (empty schema) |
| 2 ingest, 8 deliveries | 1.4 s | 119 MB | 6.4 s | 165 MB | 12.0 s | 208 MB | 136 MB |
| 3 run.process | 3.3 s | 286 MB | 12.5 s | 1,039 MB | 27.5 s | 1,980 MB | 795 MB |
| 4 reprocess (v2) | 3.1 s | 313 MB | 12.1 s | 1,208 MB | 26.7 s | 2,058 MB | 1,517 MB |
| 5 API reads (peak includes 4 clients) | n/a | 1,305 MB | n/a | 4,325 MB | n/a | 4,413 MB | 1,517 MB |

Phases of run.process: 

| Phase | 10% | 50% | 100% |
|---|---|---|---|
| load | 0.14 s | 0.41 s | 0.68 s |
| evaluate (pure controls) | 0.62 s | 2.80 s | 4.77 s |
| persist (exceptions, groups, balances, audit) | 2.53 s | 9.28 s | 22.09 s |
| reprocess: load / evaluate / persist | 0.07 / 0.52 / 2.47 s | 0.32 / 2.09 / 9.68 s | 0.47 / 4.00 / 22.22 s |

Ingest at 100% (benchmark's own CSV adapter; one delivery after the other, single connection):

| Delivery | Rows | Accepted | Rejected | Time | Rows/s |
|---|---|---|---|---|---|
| transfer_instructions | 45,224 | 44,764 | 460 | 1.78 s | 25,434 |
| direct_debit_events | 20,105 | 19,902 | 203 | 0.69 s | 29,185 |
| card_transactions | 70,182 | 70,000 | 182 | 2.72 s | 25,765 |
| card_settlements | 700 | 691 | 9 | 0.02 s | 29,165 |
| internal_ledger_entries | 64,550 | 64,131 | 419 | 2.05 s | 31,468 |
| banking_transactions | 65,280 | 65,084 | 196 | 1.90 s | 34,364 |
| client_account_balances | 100,000 | 100,000 | 0 | 2.78 s | 35,949 |
| safeguarding_balances | 12 | 11 | 1 | 0.00 s | n/a |

Rejected rows are the seeded defects (missing field, duplicate); every delivery ends `QUALIFIED` except the balances (`RECEIVED`).

### API read latencies (4 concurrent clients, loopback)

p50 / p95 / p99 in ms; response body of the last request in KB.

| Endpoint | 10% | 50% | 100% | 100% body |
|---|---|---|---|---|
| control room | 57.7 / 76.0 / 83.3 | 359 / 411 / 469 | 782 / 1,048 / 1,067 | 3,078 KB |
| run detail | 0.6 / 0.9 / 2.5 | 0.7 / 0.8 / 1.5 | 0.8 / 1.1 / 1.8 | <1 KB |
| exceptions, page 1 (50) | 1.9 / 2.8 / 3.2 | 2.3 / 3.6 / 3.8 | 2.7 / 4.2 / 4.5 | 30 KB |
| exceptions, page 21 (cursor) | 26.8 / 31.2 / 40.3 | 3.2 / 7.1 / 16.8 | 3.5 / 5.1 / 5.4 | 30 KB |
| exceptions, control+severity+status filter | 18.2 / 19.4 / 19.8 | 113 / 133 / 200 | 3.0 / 4.8 / 5.1 | 27 KB |
| exceptions, blocking filter | 3.2 / 4.1 / 4.5 | 2.8 / 6.3 / 9.1 | 2.8 / 4.1 / 4.5 | 35 KB |
| exceptions, currency + value range | 3.1 / 4.4 / 4.5 | 15.0 / 17.2 / 17.8 | 29.5 / 36.5 / 42.2 | 30 KB |
| exceptions, free-text search | 11.2 / 11.9 / 12.6 | 80.4 / 89.3 / 117 | 103 / 166 / 308 | 10 KB |
| exceptions summary | 15.0 / 16.1 / 17.2 | 126 / 160 / 183 | 243 / 302 / 350 | 2 KB |
| transactions, page 1 (50) | 30.3 / 32.2 / 32.8 | 140 / 210 / 243 | 258 / 408 / 474 | 26 KB |
| transactions, kind + match status filter | 67.1 / 565 / 596 | 303 / 318 / 330 | 520 / 537 / 537 | 27 KB |
| transactions of one account | 2.8 / 3.4 / 3.6 | 3.5 / 4.2 / 4.5 | 3.8 / 4.6 / 5.4 | 2 KB |
| accounts, page 1 (50) | 24.7 / 28.9 / 29.3 | 108 / 114 / 116 | 182 / 205 / 215 | 18 KB |
| account ledger | 5.7 / 6.9 / 7.1 | 8.1 / 10.1 / 12.9 | 7.7 / 9.1 / 9.6 | 2 KB |
| safeguarding position | 129 / 154 / 169 | 715 / 894 / 920 | 1,382 / 1,612 / 1,612 | 8,909 KB |
| run sign-off | 488 / 571 / 591 | 3,065 / 3,838 / 3,838 | 8,187 / 8,354 / 8,354 | 77,040 KB |

No request failed in the final runs. Endpoints with fewer than about 100 samples (sign-off: 5 requests at 100%) have a p95/p99 that is just the maximum.
Some filtered lists show plan variance between scales rather than a trend (10%: exception page 21 and the match-status transaction filter p95; 50%: the
control+severity+status exception filter at 113 ms against 3 ms at 100%). The database is loaded just before the read stage and `ANALYZE` is not run, so
plan choice after a large load depends on whether auto-analyze has caught up (see Findings not fixed, item 6); read these as variance, not as a trend.

### Database size at 100% (two run versions, after reprocess)

| Table | Size | Rows |
|---|---|---|
| exceptions | 306 MB | 272,728 |
| audit_events | 252 MB | 272,769 |
| match_candidates | 180 MB | 851,030 |
| match_groups | 167 MB | 524,458 |
| exception_rule_traces | 149 MB | 272,728 |
| exception_timeline | 130 MB | 545,456 |
| account_control_results | 87 MB | 200,000 |
| match_group_exceptions | 55 MB | 266,942 |
| exception_links | 49 MB | 272,844 |
| the six record tables with the ingested evidence rows | about 122 MB | 364,583 rows (one copy) |

Database size grew from 136 MB (after ingest) to 795 MB for the first run version and by another 722 MB for the reprocessed version, at this
exception volume; the evidence rows (one copy) are small by comparison.

### Slowest statements

Totals over the stage, `n` executions, at 100%. After the fixes the persistence statements are the largest write cost in run.process (bulk
`INSERT ... SELECT FROM unnest`, 1,000 rows per execution): `INSERT INTO exceptions` 4.45 s (137 executions, max 61 ms), `match_candidates` 3.44 s (263,
max 268 ms), `audit_events` 3.05 s (146, max 27 ms), `match_groups` 2.70 s (263, max 27 ms). During ingest the largest are the record inserts
(`account_balances` 2.3 s, `card_events` 2.2 s). In the read stage the transaction-list statement (all variants) accounts for
75.8 s over 1,278 executions (max 396 ms), the exception list for 49.7 s over 2,346 (max 773 ms), the control-room `SELECT delivery_id, id FROM exceptions`
for 10.6 s over 48 (about 220 ms each, it returns every exception id of the run), and five single-pass `GROUP BY` statements of the exception summary for about 6.3 s each over 137.

## Bottlenecks found and fixes

All fixes keep behaviour: the existing tests (`cd backend && dotnet build -warnaserror && dotnet test`, all test projects including the acceptance tests that
reproduce `expected_findings.csv`) pass unchanged, and the run result hash is byte-identical before and after (same inputs): `ca2c1108f7f4` at 3% on the
unmodified code and on the fixed code, `1faa858e2a5e` at 10% (before: baseline run; after: re-run with `--distinct-refs`, which regenerates the same data as
the baseline) with the same 22,467 exceptions.

"Before" numbers were taken on the code as committed at the start of the spike. Where "before" was not measured at the same scale it says so; extrapolations
are labelled and are not measurements.

| # | Defect | Fix | Before | After |
|---|---|---|---|---|
| 1 | **Quadratic control logic.** `MatchingControl.Step` compared every internal record with every counterpart record for each of seven rules and each pair; every committed group did a full `RemoveAll` over both lists; `Grouped` rescanned the counterpart list per reference; `IndependentLedgerControl` scanned all ledger entries once per account; `RunEvaluator` rebuilt the record index of a pair for every match group (up to 100,000 groups). | Candidate edges come from a reference or (currency, amount, date) index and the rule predicate is still applied to every candidate in list order, so the edges and their order are exactly those of the cross product; lazy removal compacted once per rule; per-pair and per-account indexes built once. `MatchItem.Key` computed once. | 10%: run.process 397.4 s, of which evaluate 343.2 s; reprocess 243.0 s, of which evaluate 201.2 s. 100%: not run (extrapolated from the quadratic growth: more than 9 hours; not a measurement). | 10% (same data): evaluate 0.78 s (reprocess 0.60 s), about 440x. 100%: evaluate 4.77 s. 3% (same data, before and after measured): process 10.1 s -> 1.1 s. |
| 2 | **Row-by-row persistence.** Every exception, rule trace, link, timeline entry, match group, candidate, group-exception link and account result was a separate `INSERT` round trip, plus 4 statements per audit event (ensure chain head, lock, insert, update head). | `RunBulkPersistence`: one `INSERT ... SELECT FROM unnest(...)` per table and chunk of 1,000 rows, in the same transaction, same `ON CONFLICT DO NOTHING` and "child only for an inserted parent" semantics, same order of children; carried-over history copied set-based per table. `IAuditWriter.AppendManyAsync` (default interface method, so fakes keep working; `AuditWriter` implements it): chain head locked and read once, hash chain computed in memory, one insert per 1,000 events. | 10% (same data): persist 53.7 s (reprocess 41.7 s); the 4 audit statements alone were 27.2 s of it (22,476 events). | 10% (same data): persist 3.8 s (reprocess 3.95 s), about 14x. Audit inserts: 23 executions and about 0.35 s for 13,693 events (10%, default data). 100%: persist 22.1 s. |
| 3 | **Missing index for the exception lookup of the transaction and account views** (`exceptions (run_id, delivery_id, source_row_id)`): for each of the ~264,000 records of the run the database scanned the run's exceptions. | Migration `0048_performance_indexes.sql`: `ix_exceptions_run_delivery_row`, `ix_exceptions_run_source_row`. | 10%: one transaction page took 30 s and hit the 30 s command timeout (HTTP 500); the same for accounts and the account ledger. | 10%: 285 ms of database time for the same unpaged evaluation, then fix 4 below. |
| 4 | **Page-last enrichment** (transaction list, single transaction, account list, single account, account ledger): lifecycle (window function), match group and exceptions were computed for every record of the run, then sorted and cut to 50 rows; the account view tested every account against every exception (a quadratic join); the aggregates repeated the whole evaluation. | The page is selected and ordered on the cheap record columns first and only its rows are enriched (per-row index lookups; lifecycle through the per-table key indexes, equivalent to the window function: verified identical on all 26,370 records of the 10% run); account exceptions in three index-friendly branches (verified equal to a recomputation from the transaction view for 10,000 accounts); `@acct`/page scope pushes the account filter down; a filter on match status, exception presence or account state keeps the earlier whole-run evaluation. | 10% (original): transactions page timeout (30 s); transactions of one account timeout; accounts page timeout; account ledger timeout. | 10%: transactions page 30 ms, one account's transactions 2.8 ms, accounts page 25 ms, account ledger 5.7 ms. 100%: 258 ms, 3.8 ms, 182 ms, 7.7 ms. (Intermediate 100% measurement with fixes 1-3 and only the account branch rewrite: accounts page 2,575 ms, account ledger 303 ms.) |
| 5 | **Exception list ordering**: the default sort `-severity,dueAt,id` sorted every exception of the run for each page. | Expression index `ix_exceptions_run_default_order` that matches `ExceptionSortSpec` exactly (same migration). | 100% (measured with fixes 1-3 and the transaction-list part of 4 in place): first page 144 ms, page 21 156 ms, control+severity+status filter 101 ms, blocking filter 33 ms. At 10% (original): 45.7 / 54.8 / 64.7 / 19.8 ms. | 100%: 2.7 / 3.5 / 3.0 / 2.8 ms. |

Also changed: `RunProcessor` writes diagnostic phase activities (`ActivitySource "Proofly.Runs"`: `run.load`, `run.evaluate`, `run.persist`; no cost without a listener).
`CarryOver.CopyHistoryAsync` was replaced by the set-based copy.

Files changed: `Proofly.Controls/Matching/MatchingControl.cs`, `MatchingModels.cs`, `Ledger/IndependentLedgerControl.cs`;
`Proofly.Data/Orchestration/RunEvaluator.cs`, `RunProcessor.cs`, `RunBulkPersistence.cs` (new), `CarryOver.cs`; `Proofly.Data/Audit/AuditWriter.cs`;
`Proofly.Data/Transactions/TransactionSql.cs`, `TransactionReadRepository.cs`; migration `0048_performance_indexes.sql`; the benchmark project.

## Findings not fixed

These need a decision by the owner of the control or of the contract; the spike did not change behaviour or the contract.

1. **Matching control: unmatched bank rows are reported once per pair they do not belong to.** `MatchingInput.FromRecords` gives the same bank list as the
   counterpart side of four pairs (instruction, ledger, direct debit, card settlement). A bank row that belongs to a transfer therefore also appears as
   "no internal candidate" in the direct-debit and settlement pairs. In the 100% run 97% of 136,364 exceptions per run version are `NO_CANDIDATE` (at 10%: 13,289 of
   13,684). This inflates exception counts, persistence, database size and the size of several responses roughly threefold; whether it is intended
   (pair scoping) or a defect is for the matching control's owner to decide. All volumes in this document are for the behaviour as implemented.
2. **Unbounded response bodies, fixed by the contract.** `GET /runs/{id}/sign-off` returns every open exception in `openMatters`: 77 MB and 8.2 s p50 at 100%
   (136,364 open exceptions); `GET /runs/{id}/safeguarding` returns every obligation line: 8.9 MB, 1.4 s; `GET /runs/{id}/control-room` carries every exception id per
   delivery: 3 MB, 0.8 s. These sizes are properties of the response shape in `openapi.yaml`, not of a slow query; a fix is a contract change (counts plus a link
   or a page), so this is raised, not implemented.
3. A filter on **match status or exception presence** on the transaction list still evaluates the whole run (about 520 ms at 100%); an account **state** filter
   likewise. Free-text search is an unindexed `ILIKE '%...%'` (about 100 ms on 136,000 exceptions at 100%).
4. **Exception summary** issues five single-pass aggregates (243 ms p50 at 100%); one grouped pass would be about a fifth of that.
5. **Memory**: run.process peaks at about 2 GB of working set for 100,000 transactions (286 MB / 1,039 MB / 1,980 MB at 10/50/100%, about 19 MB per 1,000
   transactions); all records, groups, findings and the persisted JSON are held in memory for the single processing transaction. This is the limit that
   would hit first when the volume grows (extrapolation, not measured: about 19 GB at 1,000,000 transactions).
6. **Statistics**: the benchmark does not run `ANALYZE` after loading. Auto-analyze timing can change plans right after a large load (the 10% outliers above).
   A post-ingest `ANALYZE` of the record and result tables is worth considering.
7. **Audit chain**: all run events go to the global chain `*`, whose head row is locked for the whole processing transaction; concurrent runs for different
   entities serialise on it. Not measured (the spike ran one run at a time).
8. Not measured: `LineageRepository` still uses the whole-run transaction evaluation to find the transaction of an exception; evidence-pack generation; the
   write commands (resolutions, sign-off approve); concurrent processing; ingest with several deliveries in parallel.

## Projected headroom (extrapolation from three scales, not measurements)

Against the stated target envelope of 10,000-100,000 transactions per day and 10,000-100,000 client accounts, on this machine and with this synthetic
volume: ingest (12 s) plus processing (27 s) is well under a minute per 100,000-transaction day, a reprocess costs another 27 s, and the interactive
endpoints that return pages answer in single-digit to a few hundred milliseconds. Time grows about linearly between the three scales for ingest,
evaluate and persist (persist 2.5 / 9.3 / 22.1 s for 10 / 50 / 100%), so doubling the volume would be expected to roughly double the batch times and
memory. Memory (about 2 GB at the top of the envelope) and database growth (about 0.8 GB per run version at 100,000 transactions with this exception volume,
and roughly the same again per reprocess) are what would constrain a longer retention or a larger volume first, not CPU. None of this is a capacity
statement for a customer: their data shape, exception rate, hardware, PostgreSQL configuration, concurrency, network and storage will differ.

## What these measurements do and do not prove

They prove that, on one Apple M4 machine with a stock PostgreSQL 14 on local SSD, the implementation at the commit of this work ingested, processed
and reprocessed a synthetic 100,000-transaction / 100,000-account day within the times above, reproduced identical result hashes across code changes and
across both CSV adapters, and answered the listed read requests with the listed latencies under 4 concurrent clients; and that the defects in the table
existed and are fixed.

They do **not** prove anything about:

- customer environments: different CPU, memory, disks (network storage, virtualisation), PostgreSQL version and configuration, antivirus, other load;
- real data: the data is synthetic and regular (uniform account choice, three currencies, one legal entity, one business date, one delivery per source,
  ids and references that join cleanly), the defect mix is invented, and real files have wider rows, longer text, other encodings and quoting;
- the network: client, API and database are on loopback, so no latency, bandwidth or TLS cost is included; response bodies of 8-77 MB would matter on a network;
- concurrency beyond four read clients: no parallel processing of runs, no concurrent writers, no mixed read/write load;
- long-term behaviour: one day of data in a fresh database, no vacuum/bloat history, no retention, no backups running;
- production hosting: Local authentication was used, no reverse proxy, no TLS, no OpenTelemetry/Serilog output;
- the production CSV, Excel or XML adapters in general (one 100% ingest with the production CSV adapter is reported; the others were not measured);
- statistical confidence: each stage was run once per scale (the API stage has hundreds of samples per endpoint); repeat runs showed up to about 30%
  difference on the slower endpoints because the machine was shared.

The acceptance plan's open performance targets (ingestion deadline, T+1 run deadline, interactive response, evidence-pack time, concurrent users) remain
to be agreed and tested in the target environment.

## Reproducing

```
cd backend
dotnet build tools/Proofly.Benchmarks -c Release
dotnet run -c Release --no-build --project tools/Proofly.Benchmarks -- --scale 1.0 --out /tmp/bench-1.0.json
```

Options: `--scale <0.1|0.5|1.0|...>`, `--out <file.json>`, `--keep` (keep the database `proofly_bench`), `--skip-api`, `--api-only` (reuse a kept database, run the read stage),
`--api-seconds <n>`, `--connectors-csv` (production CSV adapter), `--distinct-refs` (bank reference differs from the instruction id, as in the synthetic fixture,
which adds unmatched ledger and bank rows), `--db <name>`. Needs the local PostgreSQL at localhost:55432 (user `proofly`, trust); the tool drops and creates
its own database and writes all generated files to a temp directory that it deletes at the end.
