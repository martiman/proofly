# Independent Ledger Control

## Status

Proposed oMVP control specification.

## Objective

Reconstruct client-account balances independently from supported evidence and compare them with balances recorded by the primary ledger.

## Inputs

- independently supported opening balances;
- canonical transaction events and ledger movements;
- account, currency and legal-entity mappings;
- versioned posting, sign and value-date rules;
- internal recorded closing balances; and
- completeness results for contributing sources.

## Preconditions

- one explicit currency per account calculation;
- approved account identity and mapping;
- an opening balance with defined provenance; and
- accepted or visibly qualified contributing evidence.

## Calculation

For each client account and currency:

```text
independent closing balance
  = supported opening balance
  + supported credits
  - supported debits
  + approved adjustments

balance difference
  = independent closing balance
  - recorded closing balance
```

Debit and credit direction follows the active posting-rule version. Calculations use fixed decimal precision and never perform implicit currency conversion.

## Deterministic checks

- opening balance is present and supported;
- each canonical movement is applied once;
- entries are balanced where the source model requires it;
- signs and dates follow the configured rules;
- no incompatible duplicate movement is included;
- calculated and recorded balances are compared at the same scope and date; and
- any tolerance is explicit and does not hide the underlying difference.

## Outputs

- opening balance;
- debit and credit movement totals;
- approved adjustments;
- independent closing balance;
- recorded closing balance;
- difference;
- contributing-record trace; and
- control result per account and aggregated scope.

## Initial reason codes

- `OPENING_BALANCE_MISSING`;
- `OPENING_BALANCE_UNSUPPORTED`;
- `MOVEMENT_UNMAPPED`;
- `MOVEMENT_DUPLICATE`;
- `INVALID_SIGN`;
- `INVALID_VALUE_DATE`;
- `RECORDED_BALANCE_MISSING`;
- `BALANCE_DIFFERENCE`; and
- `CALCULATION_SCOPE_MISMATCH`.

## Exception rules

A material account difference creates an exception containing the value, currency, account, contributing movements and rule version. Missing opening or recorded balances create separate exceptions because they prevent a valid comparison.

## Reprocessing

The same evidence and ruleset must produce identical balances. A changed mapping, posting rule or corrected delivery creates a new run version and calculation trace.

## Acceptance examples

- One internal ledger credit is missing: the difference equals that movement and links to its external evidence.
- A movement is delivered twice with the same source identifier: it is included once and a duplicate exception is raised.
- Opening balance is absent: no closing-balance pass is produced.

