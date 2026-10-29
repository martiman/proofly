# Synthetic dataset findings report (issues #18, #28, #38)

Status: analysis of two versions of `examples/synthetic-data/2026-10-27` run through the real pipeline (registry, ingestor, CSV adapters, `run.process`) by `SyntheticDatasetLoader` and compared with their own `expected_findings.csv` by `backend/tests/Proofly.Acceptance.Tests/Findings`.

- **Committed dataset** (in the repository): `LE-CZ-001`, EUR/CZK/USD, `+01:00`.
- **Reworked dataset** (product owner, uncommitted at the time of writing, analysed from a copy): `LE-UK-001`, GBP/EUR/USD, `+00:00`, invalid currency `UKP`, manifest columns `declared_total_gbp`/`declared_total_eur`. The structure of both datasets is identical, so every result below holds for both unless a row says otherwise (EUR becomes GBP, CZK becomes EUR, `EURO` becomes `UKP`).

Neither dataset file was modified. Re-run after any dataset change: `PROOFLY_DATASET_DIR=<dir> dotnet test backend/tests/Proofly.Acceptance.Tests --filter FullyQualifiedName~Findings` (strict, no allow-lists, see 4).

## 1. Result

| | committed dataset | reworked dataset |
|---|---|---|
| expected findings | 29 | 29 |
| reproduced (same control, reason code, severity, source file and row) | 26 | 26 |
| not reproduced | 3 (F-013, F-023, F-028) | 3 (same) |
| produced exceptions not in `expected_findings.csv` | 20 allow-listed conditions, all produced by documented rules | same conditions |
| severity mismatches | 0 | 0 |

The test on the committed dataset passes with two explicit lists (3.2, 3.3): `Findings/expected_extra_findings.csv` (extras) and `Findings/known_dataset_gaps.csv` (the 3 findings the rules cannot produce). Both apply only to the committed dataset (the reworked one passes with the same lists via `PROOFLY_EXTRA_FINDINGS_FILE` / `PROOFLY_KNOWN_GAPS_FILE`, and fails strictly without them). A gap that starts to be reproduced fails the test until removed from the list.

## 2. Pipeline fixes made (with tests in `Proofly.Controls.Tests/Matching/MatchingInputFromRecordsTests.cs`)

Before the fixes the committed dataset produced 66 exceptions (73 with the production count-mismatch policy), several of the expected findings were attributed to another record (F-007, F-008, F-009, F-010) or had no producing rule (F-024), and 7 ledger NO_CANDIDATE and 2 AMBIGUOUS_CANDIDATES exceptions were mapping artefacts. Now: 55.

1. **Attribution (`Finding.Primary`)**: an exception's source file and row were taken from the first of a finding's supporting records (sorted alphabetically in the ledger control, `[transfer, ledger]` in lifecycle). They now come from the record the finding is about: ledger INVALID_SIGN and lifecycle INVALID_STATE on the ledger entry (F-007, F-008), BALANCE_DIFFERENCE on the recorded balance (F-009, F-010), RETURN_NOT_POSTED on the returned event, matching findings on the counterpart (bank, presentment) record (F-011, F-012, F-024).
2. **Ledger reference mapping**: the bank carries the instruction's external reference (`BANKREF-001`), the ledger the instruction id (`TRF-001`); the lifecycle control already joined them, matching did not, so every transfer ledger entry was NO_CANDIDATE. A ledger reference naming a known instruction now maps to that instruction's external reference.
3. **Direct debit pair**: SUBMITTED, RETURNED and REJECTED events move no money to the bank. Only the SETTLED collection takes part; submitted and settled events of the same debit made the bank credit AMBIGUOUS_CANDIDATES (BTX-006, BTX-007).
4. **Rejected and cancelled instructions** are not matched to the bank (no bank movement is expected; lifecycle already checks them, INVALID_STATE F-008). They produced NO_CANDIDATE false positives (TRF-005, TRF-007).
5. **New matching pair `card_authorisation_to_presentment`** (value only, only for card transactions that have both events): F-024 (authorisation 60 against presentment 65) had no producing rule. Missing authorisation or presentment stays the lifecycle control's finding.
6. **No double reporting of one bank record**: a bank record that is matched, value-mismatched, ambiguous or currency-mismatched in one pair is no longer also reported as "unmatched in every pair"; a value mismatch against the same bank record seen through several pairs (instruction, ledger, direct debit) is reported once, in the first pair by name (F-011, F-012 were 2 exceptions each).

Not changed (rules unchanged, nothing loosened): ingestion rules, severities, tolerances, completeness, safeguarding. The default ingestion policy (count mismatch REJECTS) is unchanged; the loader/test fixture QUALIFIES a count mismatch (see 3.3 F-014).

## 3. Per-finding results (both datasets)

### 3.1 Reproduced (26)

| id | expected | produced as |
|---|---|---|
| F-001 F-003 F-004 F-005 F-015 F-017 F-021 F-022 F-027 | completeness rejections (missing field, invalid value) | same file and row, HIGH |
| F-002 F-018 | DUPLICATE_RECORD SRC-T-010, SRC-S-010 | same, HIGH (first occurrence accepted) |
| F-006 | MISSING_INSTRUCTION SRC-D-010 | same |
| F-007 | INVALID_SIGN LED-004 | same (after fix 1) |
| F-008 | INVALID_STATE LED-005 | same (after fix 1) |
| F-009 F-010 | BALANCE_DIFFERENCE CA-004, CA-005 | row id is the natural key `CA-004\|DAILY_ROLLFORWARD\|2026-10-27`; the test accepts the account id as prefix |
| F-011 F-012 | VALUE_MISMATCH BTX-009, BTX-010 | same, MEDIUM, one exception each (after fix 6) |
| F-014 | CONTROL_COUNT_MISMATCH, `delivery_manifest.csv`, `banking_transactions` | delivery-level exception on `banking_transactions.csv`, no row, blocking; the test maps a manifest reference to the delivery file. Needs the loader's count-mismatch policy (below) |
| F-016 | EXTERNAL_BALANCE_STALE SRC-S-007 | same |
| F-019 | SHORTFALL CRITICAL, MULTIPLE | SRC-S-001 (first of the contributing rows), blocking. Committed: EUR 19000 against 21124.51; reworked: GBP side |
| F-020 | CONTROL_VALUE_MISMATCH SRC-C-003 | row rejection, same |
| F-024 | VALUE_MISMATCH SRC-K-004 | same (after fix 5) |
| F-025 F-026 F-029 | MISSING_AUTHORIZATION SRC-K-007, MISSING_ORIGINAL_TRANSACTION SRC-K-008, MISSING_SETTLEMENT SRC-K-010 | same |

F-014 and the policy: with the production default (count mismatch REJECTS the delivery) the bank evidence is not stored, F-011, F-012 and F-013 cannot be produced at all and every bank-related pair reports NO_CANDIDATE. The expected effect of F-014 says "reject or qualify"; the fixture needs "qualify". `SyntheticDatasetLoader` therefore sets `CountMismatch = Qualify` (option `QualifyCountMismatch`, CLI `--reject-count-mismatch` restores the default). Rule question for the product owner: should the fixture's expected effect say "qualify"?

### 3.2 Produced, not in the catalogue (all genuinely produced by documented rules; allow-listed for the committed dataset)

| condition | rule | cause in the data | recommendation |
|---|---|---|---|
| 5 CONTROL_VALUE_MISMATCH at delivery level (card_settlements, client_account_balances, direct_debit_events, safeguarding x2) | completeness: declared totals equal row totals | see section 5 | fix data (manifest) |
| CALCULATION_SCOPE_MISMATCH LED-010 | ledger: no conversion or netting across currencies | EUR (GBP in the reworked dataset) entry for DD-003 on CA-003 whose balance is CZK (EUR) | fix data (use the account's currency) or list in catalogue |
| NO_CANDIDATE BTX-008 | matching | the return debit `DD-002-RETURN` has no internal record (the return event is keyed `DD-002`) | fix rule if returns should match by reference convention, else list in catalogue |
| NO_CANDIDATE LED-005 | matching | posted debit of the rejected TRF-005 has no bank movement (the same defect as F-008) | list in catalogue |
| NO_CANDIDATE and VALUE_MISMATCH on 7 card settlements (SRC-C-001, 002, 004, 005, 006, 007, 010) | matching: settlement to bank (net) and presentments to settlement (gross) | the bank file has no card credits; presentments (185, 80) do not add up to the settlement gross (1000, 500); settlements 004 to 007 and 010 have no presentments | fix data (add bank credits and consistent presentments) or list in catalogue |
| NO_CANDIDATE SRC-K-010 | matching | presentment references the unknown settlement CARD-999 (same defect as F-029) | list in catalogue |
| INVALID_TRANSITION SRC-D-004 | lifecycle: only SETTLED to RETURNED is allowed | DD-002 has SUBMITTED then RETURNED, no SETTLED event, while bank (BTX-007) and ledger (LED-009) show the collection | fix data (add the SETTLED event; row count and totals change) |
| MISSING_AUTHORIZATION SRC-K-010 | lifecycle | CTX-007 presentment has no authorisation (as CTX-004, F-025) | list in catalogue |
| RETURN_NOT_POSTED SRC-D-004, SRC-D-010 | lifecycle | DD-002 is F-013 (attribution, 3.3); DD-006 return has no reversal either | list in catalogue |

Severities are taken from the rules (matching MEDIUM, others HIGH).

### 3.3 Expected but not reproduced (known gaps, committed dataset only)

| id | why | classification | recommendation |
|---|---|---|---|
| F-013 RETURN_NOT_POSTED `banking_transactions.csv` BTX-008 | the exception is attributed to the return event SRC-D-004 (produced and allow-listed with the same control, reason, severity). The lifecycle control has no rule that links a direct debit return to a bank row (`DD-002-RETURN`) | expected file points at another record than the control's subject | change the expected row to `direct_debit_events.csv`, `SRC-D-004`; or specify a return-evidence reference for the bank file |
| F-023 DUPLICATE_RECORD `card_settlements.csv` SRC-C-010 | `CARD-009` first appears in SRC-C-009, rejected for its invalid currency; SRC-C-010 is the first accepted `CARD-009`, so it is not a duplicate of an accepted record (rule: first occurrence kept, a rejected row is not a record) | data/rule question | fix data (give the duplicate its own valid first occurrence) or decide that identifiers of rejected rows reserve their key |
| F-028 DUPLICATE_RECORD `card_transactions.csv` SRC-K-010 | same for `CEV-009` (first in SRC-K-009, rejected) | same | same |

## 4. Test design

`Findings/DatasetFindingsTests` (xUnit, runs in CI, needs `proofly_test`): loads `PROOFLY_DATASET_DIR` (default: committed dataset) into a per-test schema, compares exceptions with that directory's `expected_findings.csv`:

- every expected row needs an exception with the same control, reason code and severity and the same source file and row (lenient only for `MULTIPLE`, natural-id prefix `CA-004` and manifest rows, 3.1); otherwise it is reported MISSING (or SEVERITY);
- every exception must be explained by an expected row or by an allow-list row of the same control, reason, severity and source reference; otherwise UNEXPECTED. Several exceptions may explain one expected row;
- the report lists missing, severity mismatches and unexpected exceptions, known gaps and allowed extras;
- `PROOFLY_SKIP_DATASET_FINDINGS=1` skips it explicitly with that reason in the test report; there is no other skip;
- a second test loads the dataset twice and checks that nothing is registered, ingested, processed or audited twice.

## 5. Manifest control totals (issue #28)

Basis implemented and documented (`DeliveryIngestor`): the declared total per currency must equal the sum of the control amount (the amount; `gross_amount` for settlements) of every row of the file whose currency is readable, rejected rows included; a measured currency with no declared total counts as declared 0 once any total is declared. Decimal arithmetic. "Counted" below means the declared figure equals that sum.

| file | currency | declared | file | difference (file - declared) | basis of the declared figure | verdict |
|---|---|---:|---:|---:|---|---|
| transfer_instructions | EUR / GBP | 5065.73 | 5065.73 | 0 | all rows incl. rejected | ok |
| transfer_instructions | CZK / EUR | 18200 / 728 | same | 0 | all rows | ok |
| direct_debit_events | EUR / GBP | 1090 | 1030 | -60 | counts the invalid-currency row SRC-D-009 (60 `EURO`/`UKP`) as EUR/GBP | accidental defect (basis conflicts with card_transactions); no expected finding |
| internal_ledger_entries | both | 3995.49 and 18200 / 728 | same | 0 | all rows | ok |
| client_account_balances | EUR / GBP | 22669.49 | 22874.49 | +205.00 | none consistent: the rows sum to 22874.49 (8870 + 4604.5 + 2999.99 + 750 + 250 + 1400 + 1000 + 3000); 205.00 coincides with the bank amount of BTX-009 | accidental defect (issue #28); no expected finding |
| client_account_balances | CZK / EUR | 131800 / 5272 | same | 0 | all rows | ok |
| banking_transactions | both | 3835.49 and 18200 / 728 | same | 0 | all rows | ok. Count: declared 9, file 10 = F-014, intended seeded defect (expected finding exists) |
| safeguarding_balances | EUR / GBP | 36950 | 36950 | 0 | all rows incl. negative, evidence-less and duplicate rows | ok |
| safeguarding_balances | CZK / EUR | 350000 / 14000 | 450000 / 18000 | +100000 / +4000 | leaves out the stale row SRC-S-007 (100000 CZK / 4000 EUR) while the first column counts rows that are rejected | accidental defect (inconsistent basis); stale balance is already F-016 |
| safeguarding_balances | USD | (no column) | 5000 | +5000 | the manifest format has no USD total column, the pipeline treats it as declared 0 | rule/basis question: either a measured currency without a declared total is "not declared" (rule change in the ingestor) or the manifest gets a USD column; no expected finding |
| card_transactions | EUR / GBP | 625 | 625 | 0 | rows with a valid currency (excludes SRC-K-009, `EURO`/`UKP` 50; all rows would be 675) | ok |
| card_settlements | EUR / GBP | 7850 | 7200 | -650 | counts SRC-C-009 (650, invalid currency) as EUR/GBP | accidental defect (same inconsistency as direct_debit_events) |

Findings of the analysis:

1. **No manifest total mismatch is an intended seeded defect.** The README lists exactly one manifest condition ("a manifest record-count mismatch", F-014); no total mismatch has an expected finding, and the mismatches follow no pattern (balances: arithmetic; debits and settlements: invalid-currency row counted; card transactions: not counted; safeguarding: stale row left out in one currency only).
2. **Issue #28 (balances 22669.49 against 22874.49)**: decide option A, the manifest is wrong. Correct `declared_total_eur` (`declared_total_gbp` in the reworked dataset) to 22874.49. The web copy named in the issue (`apps/web/src/api/data/delivery_manifest.csv`) no longer exists in this worktree; check any remaining copy.
3. **Basis to document in `input-contracts.md` and the dataset README**: declared totals are by currency over every readable row incl. rejected rows; a row whose currency is not a valid ISO 4217 code belongs to no currency and cannot be declared. With that basis the manifests of the two datasets need these corrections: direct_debit_events 1030, card_settlements 7200, client_account_balances 22874.49, safeguarding_balances CZK 450000 (EUR 18000 in the reworked dataset) and a USD column (or accept 5000 USD as undeclared by changing the ingestor rule). After them the five CONTROL_VALUE_MISMATCH extras disappear.
4. The count-mismatch policy (3.1 F-014) is a rule question, not a data defect.

## 6. Recommendations (summary)

| item | action |
|---|---|
| manifest totals (5) | fix data: five corrections above, then drop the first four rows of `expected_extra_findings.csv` |
| F-013 | fix expected: attribute to `direct_debit_events.csv` SRC-D-004 |
| F-023, F-028 | fix data (valid first occurrence) or decide the rule for rejected-row keys |
| DD-002 (INVALID_TRANSITION) | fix data: add its SETTLED event |
| LED-010 | fix data: ledger entry in the account's currency |
| card settlements without bank credits or consistent presentments | fix data, or extend `expected_findings.csv` with the NO_CANDIDATE / VALUE_MISMATCH rows |
| remaining extras (LED-005, SRC-K-010, DD-006, CTX-007, BTX-008) | extend `expected_findings.csv` (they follow from the seeded defects) |
| F-014 | state in the expected effect that the delivery is qualified (records kept) |
