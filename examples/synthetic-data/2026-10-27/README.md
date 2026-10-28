# Synthetic T+1 Input Dataset

This directory contains a deterministic, entirely synthetic input package for Proofly's proposed oMVP controls. It is intended for importer development, demonstrations, control-rule tests and repeatable acceptance scenarios. It contains no production or personal data.

The package represents legal entity `LE-CZ-001` for business date `2026-10-27`. All timestamps carry an explicit Central European offset in the CSV files. Monetary values use decimal notation and are accompanied by a currency code.

## Files

| File | Rows | Purpose |
| --- | ---: | --- |
| `delivery_manifest.csv` | 8 | One delivery declaration for each source file, including expected counts and totals. |
| `transfer_instructions.csv` | 10 | Bank-transfer instructions and source states. |
| `direct_debit_events.csv` | 10 | Direct-debit instructions, settlements, returns and rejections. |
| `internal_ledger_entries.csv` | 10 | Internal postings used for independent ledger and lifecycle checks. |
| `client_account_balances.csv` | 10 | Recorded closing balances for client accounts. |
| `banking_transactions.csv` | 10 | External bank evidence for matching and lifecycle checks. |
| `safeguarding_balances.csv` | 10 | External account balances and evidence references. |
| `card_transactions.csv` | 10 | Individual card authorisation, presentment, reversal and refund events. |
| `card_settlements.csv` | 10 | Card settlement batches, fees, adjustments and destinations. |
| `expected_findings.csv` | 29 | Expected control findings for automated assertions and demonstrations. |

`source_row_id`, or the source's natural identifier where no source-row field exists, connects an expected finding to the relevant input record. `MULTIPLE` is used for a finding calculated from several records.

## Deliberately introduced conditions

The data contains valid records as well as deliberately introduced issues. These are part of the fixture and must not be corrected in place:

- missing required fields;
- duplicate identifiers and duplicate balance snapshots;
- an invalid currency code;
- a manifest record-count mismatch;
- amount and settlement-control mismatches;
- an incorrect debit/credit sign;
- a posting for a rejected instruction;
- a return without its required ledger reversal;
- balance roll-forward differences;
- a stale safeguarding balance;
- a negative safeguarding balance requiring policy treatment;
- missing safeguarding evidence;
- insufficient valid safeguarding coverage;
- a lifecycle event referencing an unknown instruction;
- a card presentment without a required authorisation;
- an authorisation-to-presentment value mismatch;
- a refund referencing an unknown original card transaction; and
- a card event linked to an unknown settlement batch.

`expected_findings.csv` is the authoritative catalogue for this fixture. It states the expected control area, stable reason code, severity, description and expected effect for each issue. The catalogue describes proposed behaviour; no current implementation is implied.

## Suggested use

1. Ingest `delivery_manifest.csv` and the eight source files without changing the source values.
2. Preserve rejected records in the completeness population.
3. Run the proposed completeness, matching, ledger, lifecycle and safeguarding controls.
4. Compare produced findings with `expected_findings.csv` by `finding_id` or by source reference and reason code.
5. Re-run the same package to verify deterministic and idempotent processing.

Do not use this package as a definitive pilot-customer schema. The source-specific contracts remain to be agreed and versioned.
