# Web visual QA 2: Transaction Explorer, Account and Ledger, Matching Workbench

Method: real browser (built-in pane, viewport effectively 1024x768 at DPR 2; the 1440 emulation was not honoured by the pane), live demo API (run RUN-LE-BENCH-20261027-v1), pen frames pkblV / nzWJG / k1kVwY read through the pencil tools. Screenshots were taken in the tool session (not saved as files); scratchpad: /private/tmp/claude-501/-Users-mates-dev-androsoft-proofly--claude-worktrees-app-vue-ant-design-vite-39c172/f7307fad-4f1b-4d25-81ce-6dffe7c7f984/scratchpad (empty).

## Transaction Explorer (/transactions)
Deviations fixed
- `.ph` class of the "select a transaction" placeholder collided with the scoped child root of PageHeader, so the page title/subtitle rendered centered and indented. Renamed to `.pick`.
- Status pills wrapped to two lines ("Value mismatch"); now nowrap.
- Record details used a-descriptions that broke the account id (CA-00115 / 6) at narrow widths; replaced by a responsive key/value grid, ids nowrap.
- Direction showed raw enum (CREDIT); now "Credit". Provenance table overflowed its card; card scrolls horizontally.
- Hard-coded fallback colours replaced by tokens (--border, --accent, --surface-sunk; the old #2F5BEA was not the accent).
- Bank evidence (BANK-EUR-OPS) account was a link to a ledger that 404s; shown as plain text for BANK_EVIDENCE.
Verified: ?id= round trip, unknown id shows "not found" state, ?recordId=banking_transactions:376 resolves (node with matching recordId, e.g. BNK-376, is a valid transaction id; lineage subject kind RECORD with runId works), aggregates per currency, "25 of 5278", cursor paging button.
Open
- Pen has eyebrow, full-width search row with chips, "why this result" per row, lifecycle timeline with source/time and expected-but-missing stages, record comparison table and financial impact. The API gives only `lifecycle[]` (observed stages), no expected stages, no comparison data, no match-reason: contract additions needed for missing-stage display and "Why".
- List order is by id string (DD-111, DD-123, ... DD-47); server should order naturally or by timestamp.
- Forbidden state not exercised (token baked into the dev server).

## Account and Ledger (/accounts/<id>)
Fixed
- Currency switcher flashed all entity currencies while loading and offered currencies the account does not have; now shown only once data (availableCurrencies) or a 422 currency prompt exists.
- Balance bridge "Difference" row was always red, even at 0.00; tint now follows the server `state` (fail / warn / none). Bridge amounts right-aligned, tabular.
- Raw enums in Type/Direction/Match columns humanised; reference ids nowrap; movements table scrolls instead of clipping; columns stack below 1200px.
Verified: single-currency account, unknown account -> "not found" state, ?currency= round trip, Related transactions link to /transactions?id=.
Open
- No multi-currency account exists in the demo data, so the switcher and the 422 "choose a currency" path could not be driven live (code reviewed only). Requesting a currency the account lacks returns 404, shown as "account not found" (misleading; API could return 422/empty).
- Pen: eyebrow, header actions (compare versions, export trace), movement-type chips (Missing/Duplicated/...), difference breakdown by cause, historical comparison are not in the contract.

## Matching Workbench (/matching)
Fixed
- Three-column grid crushed the centre column below 1280px; now two columns with the right stack below. Side-by-side panes auto-fit; summary strip wraps.
- Unmatched value amounts and counts wrapped; ids and labels nowrap; table headers use the shared mono caps style.
Verified: default focus, ?transactionId=TRF-746 (highlights row, "No bank evidence found"), unknown id -> not-found state with "Open default workbench".
Not exercised (per instructions): propose / approve flows.
Open
- Pen has a field-by-field attribute comparison with result chips, candidate analysis (ambiguity, record-use conflicts, why not automatic), source pair and qualification tiles, "Rerun rule" action; the API exposes only left/right candidates, difference, rule/tolerance/rationale. Contract additions needed for attribute comparison and candidate analysis.
- Match groups list is sorted by id and 99% of rows are matched; consider defaulting the filter to non-matched.

## Tests
No pure logic changed (template/CSS fixes); existing Vitest suites unchanged. `npm run build && npm test` result: see report.
